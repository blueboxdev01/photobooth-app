using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Photobooth.Server;

/// <summary>
/// Packs up everything needed to debug a field test into one file the tester can
/// send back.
///
/// It deliberately contains **no guest photos**. The logs and the ingest decision
/// list answer nearly every question -- file names, sizes, timings, and the
/// reason for each rejection -- without a single image leaving the tester's
/// machine. Photos are the sensitive part of this application, and a diagnostics
/// bundle is the one artefact designed to be emailed around.
/// </summary>
public static class DiagnosticsBundle
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static byte[] Create(
        object diagnostics,
        string logFolder,
        IConfiguration configuration)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(zip, "README.txt", Readme);
            AddText(zip, "diagnostics.json", JsonSerializer.Serialize(diagnostics, Json));
            AddText(zip, "configuration.json", DumpConfiguration(configuration));

            if (Directory.Exists(logFolder))
            {
                foreach (var log in Directory.EnumerateFiles(logFolder, "*.log"))
                {
                    TryAddFile(zip, log, $"logs/{Path.GetFileName(log)}");
                }
            }
        }

        return buffer.ToArray();
    }

    private const string Readme = """
        Photobooth diagnostics bundle
        =============================

        diagnostics.json   Build version and commit, camera status, watch folder
                           state, the configured assumptions about EOS Utility,
                           measured press-to-file latencies, and every recent
                           ingest decision with the reason for it.
        configuration.json The effective settings this build was running with.
        logs/              Rolling application log.

        This bundle contains NO photographs. File names and sizes appear in the
        ingest log; the images themselves never leave your machine.
        """;

    /// <summary>
    /// Flattens the effective configuration, so a wrong setting is visible rather
    /// than inferred. Keys that look like secrets are masked -- the bundle gets
    /// emailed, and once Drive lands in M7 there will be a token in reach.
    /// </summary>
    private static string DumpConfiguration(IConfiguration configuration)
    {
        var lines = new StringBuilder();
        lines.AppendLine("{");

        var entries = configuration.AsEnumerable()
            .Where(kv => kv.Value is not null)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var (key, value) = (entries[i].Key, entries[i].Value!);
            if (LooksSensitive(key))
            {
                value = "***redacted***";
            }

            var comma = i == entries.Count - 1 ? "" : ",";
            lines.AppendLine(
                $"  {JsonSerializer.Serialize(key)}: {JsonSerializer.Serialize(value)}{comma}");
        }

        lines.AppendLine("}");
        return lines.ToString();
    }

    private static bool LooksSensitive(string key) =>
        key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("clientid", StringComparison.OrdinalIgnoreCase)
        || key.Contains("refresh", StringComparison.OrdinalIgnoreCase);

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static void TryAddFile(ZipArchive zip, string path, string entryName)
    {
        try
        {
            // The log file is open for writing by Serilog, so copy through a
            // shared read rather than using CreateEntryFromFile.
            using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (IOException)
        {
            // A locked log is not worth failing the whole bundle over.
        }
    }
}
