using System.Globalization;
using System.Text;

namespace DupeHunter;

/// <summary>
/// Writes a duplicate analysis to a YAML file: every duplicate file and folder set whose wasted space
/// meets the configured threshold, with all of its locations listed so the report is directly
/// actionable (e.g. feed it to a script that deletes the redundant copies). The YAML is emitted by
/// hand — there is no YAML dependency — so every string is double-quoted and escaped, which lets
/// Windows paths (backslashes) and awkward file names survive a round-trip.
/// </summary>
public static class DuplicateYamlWriter
{
    /// <summary>
    /// Serialize <paramref name="analysis"/> to <paramref name="path"/>. <paramref name="thresholdBytes"/>
    /// is the wasted-space floor the groups were filtered by (recorded in the file for context) and
    /// <paramref name="generatedUtc"/> stamps the report.
    /// </summary>
    public static async Task WriteAsync(
        string path, DuplicateAnalysis analysis, long thresholdBytes, DateTime generatedUtc, CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Duplicate file/folder report produced by dupehunter.");
        sb.AppendLine($"# Every set below wastes at least {FormatBytes(thresholdBytes)} ({thresholdBytes} bytes) of disk space.");
        sb.AppendLine("# 'wastedBytes' is the space reclaimable by keeping one copy and deleting the rest.");
        sb.AppendLine();

        sb.AppendLine($"generatedUtc: {Q(Iso(generatedUtc))}");
        sb.AppendLine($"wastedSpaceThresholdBytes: {thresholdBytes}");
        sb.AppendLine($"totalWastedBytes: {analysis.TotalWastedBytes}");

        WriteScans(sb, analysis.Scans);
        WriteGroups(sb, "duplicateFileSets", analysis.Groups);
        WriteGroups(sb, "duplicateFolderSets", analysis.FolderGroups);

        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static void WriteScans(StringBuilder sb, IReadOnlyList<ScanRef> scans)
    {
        if (scans.Count == 0)
        {
            sb.AppendLine("scans: []");
            return;
        }

        sb.AppendLine("scans:");
        foreach (ScanRef s in scans)
        {
            sb.AppendLine($"  - drive: {Q(s.Drive)}");
            sb.AppendLine($"    scanRunId: {Q(s.ScanRunId)}");
            sb.AppendLine($"    completedUtc: {Q(Iso(s.CompletedAtUtc))}");
        }
    }

    private static void WriteGroups(StringBuilder sb, string key, IReadOnlyList<DuplicateGroup> groups)
    {
        if (groups.Count == 0)
        {
            sb.AppendLine($"{key}: []");
            return;
        }

        sb.AppendLine($"{key}:");
        foreach (DuplicateGroup g in groups)
        {
            bool namesDiffer = g.DistinctNameCount > 1;
            // MIN(FileName) is only meaningful when every copy shares the one name; otherwise emit null.
            sb.AppendLine($"  - name: {(namesDiffer ? "null" : Q(g.FileName))}");
            sb.AppendLine($"    namesDiffer: {(namesDiffer ? "true" : "false")}");
            sb.AppendLine($"    contentHash: {Q(g.ContentHash)}");
            sb.AppendLine($"    sizeBytes: {g.SizeBytes}");
            sb.AppendLine($"    copyCount: {g.CopyCount}");
            sb.AppendLine($"    wastedBytes: {g.WastedBytes}");

            sb.AppendLine("    locations:");
            foreach (string p in g.SamplePaths)
                sb.AppendLine($"      - {Q(p)}");
        }
    }

    /// <summary>An ISO-8601 UTC timestamp (e.g. <c>2026-06-22T16:50:00Z</c>).</summary>
    private static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Double-quote and escape a string for a YAML double-quoted scalar.</summary>
    private static string Q(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Render a byte count as a human-friendly size (e.g. "100 MB"), for the file's comments.</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {units[unit]}";
    }
}
