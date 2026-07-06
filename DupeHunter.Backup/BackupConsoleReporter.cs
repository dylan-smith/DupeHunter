namespace DupeHunter.Backup;

/// <summary>Formats the backup analysis for the console: banner, summary, and the largest deletable entries.</summary>
internal static class BackupConsoleReporter
{
    public static void PrintBanner(BackupOptions options)
    {
        Console.WriteLine($"Backup:   {options.BackupRoot}");
        Console.WriteLine($"Database: {Path.GetFullPath(options.DatabasePath)}");
        foreach (var exclude in options.Excludes)
        {
            Console.WriteLine($"Exclude:  {exclude}");
        }

        Console.WriteLine();
    }

    public static void PrintReport(BackupReport report, int topN)
    {
        Console.WriteLine();
        Console.WriteLine("Scans consulted (latest completed per drive):");
        foreach (var s in report.Scans)
        {
            Console.WriteLine($"  {s.Drive,-6} {s.ScanRunId}  completed {s.CompletedAtUtc:u}");
        }

        var folderBytes = report.DeletableFolders.Sum(e => e.SizeBytes);
        var fileBytes = report.DeletableFiles.Sum(e => e.SizeBytes);

        Console.WriteLine();
        Console.WriteLine("Deletable from the backup (content exists elsewhere):");
        Console.WriteLine($"  Folders: {report.DeletableFolders.Count,10:n0}  ({FormatBytes(folderBytes)})");
        Console.WriteLine($"  Files:   {report.DeletableFiles.Count,10:n0}  ({FormatBytes(fileBytes)})");
        Console.WriteLine($"  Total reclaimable: {FormatBytes(report.TotalDeletableBytes)}");
        Console.WriteLine();
        Console.WriteLine("Keep (content not found outside the backup):");
        Console.WriteLine($"  Files:   {report.UnmatchedFiles.Count,10:n0}  ({FormatBytes(report.UnmatchedBytes)})");
        if (report.UnverifiableFiles.Count > 0)
        {
            Console.WriteLine("Unverifiable (never hashed; kept, and they block their folders from matching):");
            Console.WriteLine($"  Files:   {report.UnverifiableFiles.Count,10:n0}  ({FormatBytes(report.UnverifiableBytes)})");
        }

        PrintTopEntries(report, topN);
    }

    private static void PrintTopEntries(BackupReport report, int topN)
    {
        var ranked = report.DeletableFolders.Concat(report.DeletableFiles)
            .OrderByDescending(e => e.SizeBytes)
            .Take(topN)
            .ToList();
        if (ranked.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing in the backup was matched externally — nothing to delete.");
            return;
        }

        var totalDeletable = report.DeletableFolders.Count + report.DeletableFiles.Count;
        Console.WriteLine();
        Console.WriteLine($"Largest deletable entries ({ranked.Count} of {totalDeletable:n0}):");
        for (var i = 0; i < ranked.Count; i++)
        {
            var e = ranked[i];
            var kind = e.MatchKind switch
            {
                BackupMatchKind.Exact => "exact folder",
                BackupMatchKind.Superset => "folder",
                BackupMatchKind.File => "file",
                _ => "file",
            };
            Console.WriteLine($"{i + 1,4}. {FormatBytes(e.SizeBytes),10}  [{kind}]  {e.Path}");
            if (e.ExternalLocations.Count > 0)
            {
                Console.WriteLine($"      -> {e.ExternalLocations[0]}");
            }
        }
    }

    /// <summary>Render a byte count as a human-friendly size (e.g. "1.5 GB").</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {units[unit]}";
    }
}
