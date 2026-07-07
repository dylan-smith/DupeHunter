using System.IO;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui;

/// <summary>
/// Everything bound to one open YAML backup report: its path, the parsed report the views edit, and
/// the delete service. Resolving an entry — deleting it from the backup, or keeping it on purpose —
/// updates the report in memory and <see cref="SaveAsync"/> rewrites the file, so the report stays
/// the single record of what's left to review. The scan database the CLI produced it from is never
/// opened. A new session replaces the old one when another report is opened or the view is refreshed.
/// </summary>
public sealed class BackupReportSession
{
    private BackupReportSession(string reportPath, BackupReport report, int prunedOnLoad)
    {
        ReportPath = reportPath;
        Report = report;
        PrunedOnLoad = prunedOnLoad;
    }

    public string ReportPath { get; }

    public BackupReport Report { get; }

    /// <summary>Deletable entries dropped on load because they no longer existed on disk.</summary>
    public int PrunedOnLoad { get; }

    public IDeleteService DeleteService { get; } = new DeleteService();

    /// <summary>
    /// Load a report and reconcile it with the disk: deletable entries that no longer exist (deleted
    /// outside this tool) are pruned and the pruned report is written back so the file never lists
    /// dead paths.
    /// </summary>
    public static async Task<BackupReportSession> LoadAsync(string reportPath, CancellationToken ct)
    {
        var report = await BackupYamlReader.LoadAsync(reportPath, ct);
        var pruned = report.PruneMissingEntries(File.Exists, Directory.Exists);
        var session = new BackupReportSession(reportPath, report, pruned);
        if (pruned > 0)
        {
            await session.SaveAsync(ct);
        }
        return session;
    }

    /// <summary>
    /// Rewrite the report file from the in-memory state. Written to a sibling temp file and swapped
    /// in, so a failure mid-write can never truncate the report. The unmatched list stays listed only
    /// when the original report carried it (the CLI's --include-unmatched).
    /// </summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        var tempPath = ReportPath + ".tmp";
        await BackupYamlWriter.WriteAsync(tempPath, Report, includeUnmatched: Report.UnmatchedFiles.Count > 0, ct);
        File.Move(tempPath, ReportPath, overwrite: true);
    }
}
