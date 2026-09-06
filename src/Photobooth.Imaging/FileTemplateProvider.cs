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

    private static readonly JsonSerializerOptions WriteJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
    /// <remarks>
    /// Reading this loads the template if it has not been loaded yet. Otherwise
    /// it would report a meaningless default until something else happened to
    /// touch <see cref="Current"/> first -- a trap for exactly the diagnostics
    /// this property exists to serve.
    /// </remarks>
    public string Source
    {
        get { _ = Current; return _source; }
    }

    public bool UsingFallback
    {
        get { _ = Current; return _usingFallback; }
    }

    private string _source = "not loaded";
    private bool _usingFallback;

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

    /// <summary>
    /// A template name that is safe to use as a file name.
    ///
    /// Templates are named by the operator and become paths, so this is the only
    /// thing standing between a typed name and writing outside the folder.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 60
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && !name.StartsWith('-');

    /// <summary>Writes a template to disk and makes it the one in force.</summary>
    public StripTemplate Save(string name, StripTemplate template)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                "Template names may use letters, digits, dashes and underscores only.",
                nameof(name));
        }

        Directory.CreateDirectory(Folder);
        File.WriteAllText(
            Path.Combine(Folder, name + ".json"),
            JsonSerializer.Serialize(template, WriteJson));

        _logger.LogInformation(
            "Saved template {Name} with {Slots} slots.", name, template.Slots.Count);

        return Select(name);
    }

    /// <summary>
    /// Stores art for a template and works out whether it is a frame or a backdrop.
    ///
    /// JPEG is accepted as well as PNG now that art can sit behind the photos --
    /// a backdrop needs no transparency, and refusing JPEGs would rule out most
    /// of the images people actually have.
    /// </summary>
    public ArtInspection SaveArt(
        string name,
        ReadOnlySpan<byte> data,
        TemplateCanvas canvas,
        IReadOnlyList<TemplateSlot> slots)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Invalid template name.", nameof(name));
        }

        var extension = ImageExtension(data)
            ?? throw new ArgumentException(
                "The art must be a PNG or JPEG. A frame needs PNG transparency where the "
                + "photos go; a backdrop can be either.");

        var inspection = ArtInspector.Inspect(data, canvas, slots);

        Directory.CreateDirectory(Folder);

        // One art file per template whatever its format, so switching from PNG to
        // JPEG cannot leave the old one behind to be picked up later.
        foreach (var stale in Directory.EnumerateFiles(Folder, name + ".*")
                     .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(stale); } catch { /* best effort */ }
        }

        var fileName = name + extension;
        File.WriteAllBytes(Path.Combine(Folder, fileName), data.ToArray());

        _logger.LogInformation(
            "Saved art {File} ({Bytes} bytes, {Width}x{Height}) as a {Layer} layer; "
            + "{Fraction:P0} of the photo area is transparent.",
            fileName, data.Length, inspection.Width, inspection.Height,
            inspection.Layer, inspection.TransparentFractionInSlots);

        lock (_sync)
        {
            _current = null;   // reload so the new art is picked up
        }

        return inspection with { };
    }

    /// <summary>The art file stored for a template, whatever its extension.</summary>
    public string? ArtFileName(string name)
    {
        if (!IsValidName(name) || !Directory.Exists(Folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(Folder, name + ".*")
            .Select(Path.GetFileName)
            .FirstOrDefault(f => f is not null
                && !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    public string? OverlayPath(string name)
    {
        var file = ArtFileName(name);
        return file is null ? null : Path.Combine(Folder, file);
    }

    /// <summary>Magic numbers; the extension a browser sends is not evidence.</summary>
    private static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length > 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return ".png";
        }

        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ".jpg";
        }

        return null;
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

            _source = path;
            _usingFallback = false;
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
        _source = $"built-in fallback ({why})";
        _usingFallback = true;
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
