using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Photobooth.Core;

namespace Photobooth.Imaging;

public sealed class TemplateOptions
{
    public const string SectionName = "Templates";

    /// <summary>Folder holding template JSON and its frame art.</summary>
    public string Folder { get; set; } = "templates";

    /// <summary>File name of the template in force, without the extension.</summary>
    public string Selected { get; set; } = "classic-2x6";
}

/// <summary>
/// Loads strip templates from disk.
///
/// Falls back to a built-in 2x6 layout if the folder is missing or the file will
/// not parse. A booth that cannot find its template should still take photos --
/// an unbranded strip is a far better failure than a session that cannot start.
/// </summary>
public sealed class FileTemplateProvider : ITemplateProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TemplateOptions _options;
    private readonly ILogger<FileTemplateProvider> _logger;
    private readonly Lock _sync = new();

    private StripTemplate? _current;

    /// <summary>
    /// Where the template in force came from: a file, or the built-in fallback.
    ///
    /// Worth surfacing because the fallback shares the real template's name and
    /// dimensions -- so when the file is missing, everything looks right except
    /// the frame art, which is a genuinely hard thing to notice.
    /// </summary>
    public string Source { get; private set; } = "not loaded";

    public bool UsingFallback { get; private set; }

    public FileTemplateProvider(
        IOptions<TemplateOptions> options, ILogger<FileTemplateProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Folder => Path.GetFullPath(_options.Folder);

    public StripTemplate Current
    {
        get
        {
            lock (_sync)
            {
                return _current ??= Load(_options.Selected);
            }
        }
    }

    /// <summary>Templates available to choose from. Drives the picker in M8.</summary>
    public IReadOnlyList<string> Available()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(Folder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Switch templates. Changes how many photos the next session takes.</summary>
    public StripTemplate Select(string name)
    {
        lock (_sync)
        {
            _current = Load(name);
            _options.Selected = name;
            return _current;
        }
    }

    private StripTemplate Load(string name)
    {
        var path = Path.Combine(Folder, name + ".json");

        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning(
                    "Template {Path} not found; falling back to the built-in 2x6 " +
                    "layout, which has NO frame art.", path);
                return Fallback($"{path} not found");
            }

            var template = JsonSerializer.Deserialize<StripTemplate>(
                File.ReadAllText(path), Json);

            if (template is null || template.Slots.Count == 0)
            {
                _logger.LogWarning(
                    "Template {Path} has no slots; falling back to the built-in layout.", path);
                return Fallback($"{path} has no slots");
            }

            _logger.LogInformation(
                "Loaded template {Name}: {Slots} slots at {Width}x{Height}, {Dpi} DPI",
                template.Name, template.Slots.Count,
                template.Canvas.Width, template.Canvas.Height, template.Canvas.Dpi);

            Source = path;
            UsingFallback = false;
            return template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Template {Path} could not be read; falling back to the built-in layout.", path);
            return Fallback($"{path} could not be read: {ex.Message}");
        }
    }

    private StripTemplate Fallback(string why)
    {
        Source = $"built-in fallback ({why})";
        UsingFallback = true;
        return BuiltIn;
    }

    /// <summary>
    /// Classic 2x6 strip: three landscape frames over a branding footer.
    ///
    /// The slots are 4:3 rather than the camera's native 3:2 because three 3:2
    /// frames leave a dead band roughly 600 px tall at the bottom of the strip.
    /// Taller slots fill it, at the cost of cropping the sides of every photo.
    /// </summary>
    public static StripTemplate BuiltIn { get; } = new(
        Name: "Classic 2x6",
        Canvas: new TemplateCanvas(600, 1800, 300),
        Slots:
        [
            new TemplateSlot(0.0333, 0.0167, 0.9333, 0.2333),
            new TemplateSlot(0.0333, 0.2667, 0.9333, 0.2333),
            new TemplateSlot(0.0333, 0.5167, 0.9333, 0.2333),
        ],
        Overlay: null,
        Background: "#FFFFFF");
}
