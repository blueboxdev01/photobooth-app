using System.Text.Json.Serialization;
using Photobooth.Cameras;
using Photobooth.Core;
using Photobooth.Server;

// ASP.NET has its own SessionOptions (cookie sessions), which we do not use.
using SessionOptions = Photobooth.Core.SessionOptions;

// ContentRoot must be the app folder, not the shell's working directory.
// Without this, running the built DLL from anywhere but the project directory
// leaves ASP.NET unable to find wwwroot or appsettings.json -- and it fails by
// serving 404s rather than complaining, which is a miserable way to lose an hour.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<WatchFolderOptions>(
    builder.Configuration.GetSection(WatchFolderOptions.SectionName));
builder.Services.Configure<MockEosUtilityOptions>(
    builder.Configuration.GetSection(MockEosUtilityOptions.SectionName));
builder.Services.Configure<SessionOptions>(
    builder.Configuration.GetSection(SessionOptions.SectionName));

// Relative paths resolve against the app folder rather than whatever directory
// the shell happened to be in, so `dotnet run` and an unzipped published build
// behave identically -- which matters for a field-test build.
builder.Services.PostConfigure<WatchFolderOptions>(o =>
{
    o.Path = ResolveAppPath(o.Path);
    o.Extensions = o.Extensions.Length == 0
        ? WatchFolderOptions.DefaultExtensions
        : o.Extensions.Select(e => e.ToLowerInvariant()).Distinct().ToArray();
});
builder.Services.PostConfigure<MockEosUtilityOptions>(
    o => o.SourceFolder = ResolveAppPath(o.SourceFolder));

static string ResolveAppPath(string path) => Path.IsPathRooted(path)
    ? path
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

// States travel as names, not numbers: a snapshot showing "Collecting" is worth
// a great deal more than one showing 2 when reading a field tester's logs.
builder.Services.ConfigureHttpJsonOptions(
    o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services
    .AddSignalR()
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WatchFolderCamera>();
builder.Services.AddSingleton<ICameraDevice>(sp => sp.GetRequiredService<WatchFolderCamera>());
builder.Services.AddSingleton<MockEosUtility>();
builder.Services.AddSingleton<SessionEngine>();
builder.Services.AddSingleton<SessionCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionCoordinator>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<SessionHub>("/hub/session");

app.MapGet("/api/state", (WatchFolderCamera camera, SessionEngine engine) => Results.Ok(new
{
    camera = new
    {
        status = camera.Status.ToString(),
        canTrigger = camera.Capabilities.CanTrigger,
        watchFolder = camera.WatchFolderPath,
    },
    session = engine.Snapshot,
}));

app.MapPost("/api/session/arm", (SessionCoordinator c) => Results.Ok(c.Arm()));
app.MapPost("/api/session/retake", (SessionEngine e) => Results.Ok(e.RetakeLast()));
app.MapPost("/api/session/resume", (SessionEngine e) => Results.Ok(e.Resume()));
app.MapPost("/api/session/accept", (SessionEngine e) => Results.Ok(e.Accept()));
app.MapPost("/api/session/abort", (SessionEngine e) => Results.Ok(e.Abort("Aborted by operator.")));

// Stands in for a press of the BR-E1 remote. `mode` reproduces the ways EOS
// Utility is expected to misbehave -- see MockWriteMode.
app.MapPost("/api/mock/press", async (
    MockEosUtility mock, string? mode, CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<MockWriteMode>(mode ?? nameof(MockWriteMode.Normal), true, out var parsed))
    {
        return Results.BadRequest(new { error = $"Unknown mode '{mode}'." });
    }

    try
    {
        var path = await mock.SimulatePressAsync(parsed, cancellationToken);
        return Results.Ok(new { file = Path.GetFileName(path), mode = parsed.ToString() });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Serves a photo out of the watch folder. File name only -- no paths -- so a
// crafted name cannot walk out of the folder.
app.MapGet("/api/photos/{fileName}", (string fileName, WatchFolderCamera camera) =>
{
    if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
    {
        return Results.BadRequest();
    }

    var full = Path.Combine(camera.WatchFolderPath, Path.GetFileName(fileName));
    return File.Exists(full) ? Results.File(full, "image/jpeg") : Results.NotFound();
});

// /operator and /display are client-side views of one bundle.
app.MapFallbackToFile("index.html");

app.Run();
