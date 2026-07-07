using System.Globalization;
using System.Text;

namespace DupeHunter;

/// <summary>
/// Writes a <see cref="BackupReport"/> to a YAML file: every backup entry that can be deleted because
/// its content exists outside the backup tree, with the external locations that justify it. The YAML
/// is emitted by hand — there is no YAML dependency — so every string is double-quoted and escaped,
/// which lets Windows paths (backslashes) and awkward file names survive. Round-trips through
/// <see cref="BackupYamlReader"/>.
/// </summary>
public static class BackupYamlWriter
{
    /// <summary>
    /// Serialize <paramref name="report"/> to <paramref name="path"/>. Unmatched (keep) files are always
    /// counted but only listed when <paramref name="includeUnmatched"/> is set — they can vastly outnumber
    /// the deletable entries.
    /// </summary>
    public static async Task WriteAsync(string path, BackupReport report, bool includeUnmatched, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();

        sb.AppendLine("# Backup redundancy report produced by dupehunter-backup.");
        sb.AppendLine("# Every entry under deletableFolders/deletableFiles already exists outside the backup tree");
        sb.AppendLine("# (per the scan database) and can be deleted from the backup. A deletable folder is reported");
        sb.AppendLine("# whole and its contents are not listed again. Nothing was verified against the live disk —");
        sb.AppendLine("# re-scan first if the drives have changed since the scans below.");
        sb.AppendLine();

        sb.AppendLine($"generatedUtc: {Q(Iso(report.GeneratedUtc))}");
        sb.AppendLine($"backupRoot: {Q(report.BackupRoot)}");
        WriteStringList(sb, "excludes", report.Excludes, indent: "  ");
        WriteScans(sb, report.Scans);

        sb.AppendLine($"totalDeletableBytes: {report.TotalDeletableBytes}");
        WriteEntries(sb, "deletableFolders", report.DeletableFolders, withFolderFields: true);
        WriteEntries(sb, "deletableFiles", report.DeletableFiles, withFolderFields: false);

        sb.AppendLine($"unmatchedFileCount: {report.UnmatchedFileCount}");
        sb.AppendLine($"unmatchedBytes: {report.UnmatchedBytes}");
        sb.AppendLine($"unverifiableFileCount: {report.UnverifiableFiles.Count}");
        sb.AppendLine($"unverifiableBytes: {report.UnverifiableBytes}");

        WriteUnverifiable(sb, report.UnverifiableFiles);

        if (includeUnmatched)
        {
            WriteUnmatched(sb, report.UnmatchedFiles);
        }

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
        foreach (var s in scans)
        {
            sb.AppendLine($"  - drive: {Q(s.Drive)}");
            sb.AppendLine($"    scanRunId: {Q(s.ScanRunId)}");
            sb.AppendLine($"    completedUtc: {Q(Iso(s.CompletedAtUtc))}");
        }
    }

    private static void WriteEntries(
        StringBuilder sb, string key, IReadOnlyList<BackupReportEntry> entries, bool withFolderFields)
    {
        if (entries.Count == 0)
        {
            sb.AppendLine($"{key}: []");
            return;
        }

        sb.AppendLine($"{key}:");
        foreach (var e in entries)
        {
            sb.AppendLine($"  - path: {Q(e.Path)}");
            sb.AppendLine($"    sizeBytes: {e.SizeBytes}");
            if (withFolderFields)
            {
                sb.AppendLine($"    fileCount: {e.FileCount}");
                sb.AppendLine($"    matchKind: {Q(e.MatchKind == BackupMatchKind.Exact ? "exact" : "superset")}");
            }

            WriteStringList(sb, "externalLocations", e.ExternalLocations, indent: "      ", keyIndent: "    ");
        }
    }

    private static void WriteUnverifiable(StringBuilder sb, IReadOnlyList<UnverifiableEntry> entries)
    {
        if (entries.Count == 0)
        {
            sb.AppendLine("unverifiableFiles: []");
            return;
        }

        sb.AppendLine("unverifiableFiles:");
        foreach (var e in entries)
        {
            sb.AppendLine($"  - path: {Q(e.Path)}");
            sb.AppendLine($"    sizeBytes: {e.SizeBytes}");
            sb.AppendLine($"    reason: {Q(e.Reason)}");
        }
    }

    private static void WriteUnmatched(StringBuilder sb, IReadOnlyList<BackupReportEntry> entries)
    {
        if (entries.Count == 0)
        {
            sb.AppendLine("unmatchedFiles: []");
            return;
        }

        sb.AppendLine("unmatchedFiles:");
        foreach (var e in entries)
        {
            sb.AppendLine($"  - path: {Q(e.Path)}");
            sb.AppendLine($"    sizeBytes: {e.SizeBytes}");
        }
    }

    private static void WriteStringList(
        StringBuilder sb, string key, IReadOnlyList<string> values, string indent, string keyIndent = "")
    {
        if (values.Count == 0)
        {
            sb.AppendLine($"{keyIndent}{key}: []");
            return;
        }

        sb.AppendLine($"{keyIndent}{key}:");
        foreach (var v in values)
        {
            sb.AppendLine($"{indent}- {Q(v)}");
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
        foreach (var c in s)
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
}
