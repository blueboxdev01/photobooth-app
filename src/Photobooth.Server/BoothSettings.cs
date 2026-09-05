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

    public int? CountdownSeconds { get; set; }

    /// <summary>Tuned from the press-to-file latency measured during a field test.</summary>
    public int? NoPhotoTimeoutSeconds { get; set; }
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
