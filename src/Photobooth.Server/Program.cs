using Photobooth.Cameras;
using Photobooth.Core;
using Photobooth.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<WatchFolderOptions>(
    builder.Configuration.GetSection(WatchFolderOptions.SectionName));
builder.Services.Configure<MockEosUtilityOptions>(
    builder.Configuration.GetSection(MockEosUtilityOptions.SectionName));

// Relative paths resolve against the app folder rather than whatever directory
// the shell happened to be in, so `dotnet run` and an unzipped published build
// behave identically -- which matters for a field-test build.
builder.Services.PostConfigure<WatchFolderOptions>(o => o.Path = ResolveAppPath(o.Path));
builder.Services.PostConfigure<MockEosUtilityOptions>(
    o => o.SourceFolder = ResolveAppPath(o.SourceFolder));

static string ResolveAppPath(string path) => Path.IsPathRooted(path)
    ? path
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WatchFolderCamera>();
builder.Services.AddSingleton<ICameraDevice>(sp => sp.GetRequiredService<WatchFolderCamera>());
builder.Services.AddSingleton<MockEosUtility>();
builder.Services.AddSingleton<CaptureLog>();
builder.Services.AddHostedService<CameraStartup>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/operator"));
app.MapGet("/operator", () => Results.Content(Pages.Operator, "text/html"));
app.MapGet("/display", () => Results.Content(Pages.Display, "text/html"));

app.MapGet("/api/state", (WatchFolderCamera camera, CaptureLog log) => Results.Ok(new
{
    camera = new
    {
        status = camera.Status.ToString(),
        canTrigger = camera.Capabilities.CanTrigger,
        watchFolder = camera.WatchFolderPath,
        acceptFrom = camera.AcceptFrom,
    },
    photos = log.Photos.Select(p => new
    {
        p.FileName,
        p.SizeBytes,
        p.DetectedAtUtc,
        url = $"/api/photos/{Uri.EscapeDataString(p.FileName)}",
    }),
}));

// Simulates one press of the shutter release. `mode` reproduces the ways EOS
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

app.MapPost("/api/session/reset", (WatchFolderCamera camera, CaptureLog log, TimeProvider time) =>
{
    // Stands in for "a session started": everything already in the folder becomes
    // stale and is ignored from here on.
    camera.AcceptFrom = time.GetUtcNow();
    camera.ResetSeen();
    log.Clear();
    return Results.Ok(new { acceptFrom = camera.AcceptFrom });
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
    return File.Exists(full)
        ? Results.File(full, "image/jpeg")
        : Results.NotFound();
});

app.Run();

namespace Photobooth.Server
{
    /// <summary>Photos accepted so far. Replaced by the session engine in M2.</summary>
    public sealed class CaptureLog
    {
        private readonly List<CapturedPhoto> _photos = [];
        private readonly Lock _sync = new();

        public IReadOnlyList<CapturedPhoto> Photos
        {
            get { lock (_sync) { return _photos.ToList(); } }
        }

        public void Add(CapturedPhoto photo)
        {
            lock (_sync) { _photos.Add(photo); }
        }

        public void Clear()
        {
            lock (_sync) { _photos.Clear(); }
        }
    }

    /// <summary>Starts watching the folder when the app starts.</summary>
    public sealed class CameraStartup(
        WatchFolderCamera camera,
        CaptureLog log,
        ILogger<CameraStartup> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            camera.PhotoArrived += OnPhoto;
            camera.StatusChanged += OnStatus;
            await camera.ConnectAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            camera.PhotoArrived -= OnPhoto;
            camera.StatusChanged -= OnStatus;
            await camera.DisposeAsync();
        }

        private void OnPhoto(object? sender, PhotoArrivedEventArgs e) => log.Add(e.Photo);

        private void OnStatus(object? sender, CameraStatusEventArgs e) =>
            logger.LogInformation("Camera {Status}: {Message}", e.Status, e.Message);
    }
}
