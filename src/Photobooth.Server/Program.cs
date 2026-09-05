using System.Text.Json.Serialization;
using Serilog;
using Photobooth.Cameras;
using Photobooth.Core;
using Photobooth.Delivery;
using Photobooth.Imaging;
using Photobooth.Server;

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
builder.Services.Configure<SessionSettings>(
    builder.Configuration.GetSection(SessionSettings.SectionName));
builder.Services.Configure<TemplateOptions>(
    builder.Configuration.GetSection(TemplateOptions.SectionName));
builder.Services.Configure<ArchiveOptions>(
    builder.Configuration.GetSection(ArchiveOptions.SectionName));

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
builder.Services.PostConfigure<TemplateOptions>(o => o.Folder = ResolveAppPath(o.Folder));
builder.Services.PostConfigure<ArchiveOptions>(o => o.Folder = ResolveAppPath(o.Folder));

static string ResolveAppPath(string path) => Path.IsPathRooted(path)
    ? path
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

// A rolling log on disk is what a remote tester actually sends back; console
// output vanishes the moment they close the window.
var logFolder = ResolveAppPath("data/logs");
Directory.CreateDirectory(logFolder);
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logFolder, "photobooth-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true));

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
builder.Services.AddSingleton<FileTemplateProvider>();
builder.Services.AddSingleton<ITemplateProvider>(
    sp => sp.GetRequiredService<FileTemplateProvider>());
builder.Services.AddSingleton<StripCompositor>();
builder.Services.AddSingleton<SessionArchive>();
builder.Services.AddSingleton<SessionEngine>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddSingleton<SessionCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionCoordinator>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<SessionHub>("/hub/session");

app.MapTemplateEndpoints();

app.MapGet("/api/state", (WatchFolderCamera camera, SessionEngine engine) => Results.Ok(new
{
    camera = new
    {
        status = camera.Status.ToString(),
        canTrigger = camera.Capabilities.CanTrigger,
        watchFolder = camera.WatchFolderPath,
    },
    session = engine.Snapshot,
    build = new { version = DiagnosticsService.Version },
}));

// --- diagnostics: how a test in another building gets debugged ---

app.MapGet("/api/diagnostics", (DiagnosticsService d) => Results.Ok(d.Snapshot()));

// Tapped as the remote is pressed. The app cannot know when the shutter fired,
// so a human marking the moment is the only way to measure press-to-file time.
app.MapPost("/api/diagnostics/mark-press", (DiagnosticsService d) =>
{
    d.MarkPress();
    return Results.Ok(new { markedAtUtc = DateTimeOffset.UtcNow });
});

app.MapGet("/api/diagnostics/bundle", (DiagnosticsService d, IConfiguration config) =>
{
    var bytes = DiagnosticsBundle.Create(
        d.Snapshot(), ResolveAppPath("data/logs"), config);
    var name = $"photobooth-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
    return Results.File(bytes, "application/zip", name);
});

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

// Serves a file out of an archived session folder: the strip, or a raw photo.
// Both segments are constrained to a single path element so a crafted name
// cannot walk out of the archive.
app.MapGet("/api/sessions/{folder}/{file}", (string folder, string file, SessionArchive archive) =>
{
    if (!IsSafeSegment(folder) || !IsSafeSegment(file))
    {
        return Results.BadRequest();
    }

    var full = Path.Combine(archive.Root, folder, file);
    if (!File.Exists(full))
    {
        return Results.NotFound();
    }

    var type = Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    return Results.File(full, type);
});

app.MapGet("/api/sessions", (SessionArchive archive) => Results.Ok(new
{
    root = archive.Root,
    freeDiskBytes = archive.FreeDiskBytes(),
    diskIsLow = archive.DiskIsLow(),
    sessions = archive.All().Take(50),
}));

static bool IsSafeSegment(string value) =>
    !string.IsNullOrWhiteSpace(value)
    && value.IndexOfAny(['/', '\\']) < 0
    && !value.Contains("..")
    && value == Path.GetFileName(value);

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
