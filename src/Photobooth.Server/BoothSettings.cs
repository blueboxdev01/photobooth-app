using System.Text.Json;
using System.Text.Json.Serialization;

namespace Photobooth.Server;

/// <summary>
/// Choices the operator makes at the booth, as opposed to what a developer put
/// in appsettings.json.
///
/// Everything here is nullable and means "leave the configured value alone"
/// when unset, so a setting the operator never touched keeps following the
/// shipped default rather than freezing whatever it happened to be the first
/// time this file was written.
/// </summary>
public sealed class BoothSettings
{
    /// <summary>Where EOS Utility saves. Nobody knows until they look.</summary>
    public string? WatchFolder { get; set; }

    /// <summary>
    /// Where finished sessions are filed, one folder per guest.
    ///
    /// Deliberately a different root from the watch folder: that one belongs to
    /// EOS Utility and accumulates every guest's raw frames together, which is
    /// exactly what makes it useless for handing photos to a person.
    /// </summary>
    public string? OutputFolder { get; set; }

    public int? CountdownSeconds { get; set; }

    /// <summary>Tuned from the press-to-file latency measured during a field test.</summary>
    public int? NoPhotoTimeoutSeconds { get; set; }

    // --- strip layout ---

    /// <summary>Fewest photos a session may be set to at this event.</summary>
    public int? MinPhotos { get; set; }

    /// <summary>Most photos a session may be set to at this event.</summary>
    public int? MaxPhotos { get; set; }

    /// <summary>
    /// Photos per session. Must sit within the bounds above; changing it
    /// regenerates the active template's slots so the layout always matches.
    /// </summary>
    public int? PhotoCount { get; set; }

    /// <summary>Output size, which also decides portrait versus landscape.</summary>
    public string? CanvasPresetId { get; set; }

    // --- guest display ---

    /// <summary>Backdrop colour for the guest screen, so a booth can match an event.</summary>
    public string? DisplayBackgroundColor { get; set; }

    /// <summary>File name of an uploaded backdrop image, drawn over the colour.</summary>
    public string? DisplayBackgroundImage { get; set; }

    /// <summary>
    /// A working copy, so a request can be validated in full before anything is
    /// committed.
    ///
    /// <see cref="SettingsStore.Current"/> hands out the live instance, so
    /// mutating it while validating leaves a rejected request's changes in
    /// memory -- and the next successful save then writes them to disk.
    /// </summary>
    public BoothSettings Clone() => (BoothSettings)MemberwiseClone();
}

/// <summary>
/// Reads and writes <see cref="BoothSettings"/> as JSON beside the app.
///
/// Deliberately separate from appsettings.json: that file ships with the build
/// and gets replaced on upgrade, whereas these are the operator's own choices
/// and belong with their data.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _sync = new();

    public SettingsStore(string path, ILogger<SettingsStore> logger)
    {
        _path = path;
        _logger = logger;
        Current = Read();
    }

    public string Path => _path;

    public BoothSettings Current { get; private set; }

    public BoothSettings Save(BoothSettings settings)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
            Current = settings;
        }

        _logger.LogInformation("Booth settings saved to {Path}.", _path);
        return settings;
    }

    private BoothSettings Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new BoothSettings();
            }

            var settings = JsonSerializer.Deserialize<BoothSettings>(
                File.ReadAllText(_path), Json);

            if (settings is not null)
            {
                _logger.LogInformation("Loaded booth settings from {Path}.", _path);
            }

            return settings ?? new BoothSettings();
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the booth from starting.
            _logger.LogError(ex,
                "Could not read {Path}; falling back to the configured defaults.", _path);
            return new BoothSettings();
        }
    }
}
