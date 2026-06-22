namespace HarddriveDeduper;

/// <summary>
/// All user-facing console output for a scan: the startup banner, the live (in-place) progress
/// line, the final summary, and fatal-error reporting.
/// </summary>
public sealed class ConsoleReporter
{
    private readonly Options _options;

    public ConsoleReporter(Options options) => _options = options;

    /// <summary>Print what we're about to do: which drives, the hashing mode, and the destination.</summary>
    public void PrintBanner(IReadOnlyList<string> roots)
    {
        Console.WriteLine($"Scanning {roots.Count} drive(s): {string.Join(", ", roots)}");
        Console.WriteLine($"Hashing: {(_options.ComputeHash ? $"SHA-256 ({_options.Parallelism} threads)" : "disabled")}");
        Console.WriteLine($"Database: {Redact(_options.ConnectionString)}  ->  {_options.TableName}");
        Console.WriteLine();
    }

    /// <summary>
    /// Start a 1-second timer that repaints a single in-place progress line. Dispose (await) the
    /// returned handle to stop it and let any in-flight repaint finish before printing more.
    /// </summary>
    public IAsyncDisposable StartProgress(FileScanner scanner, DatabaseWriter writer) =>
        new Timer(_ =>
        {
            Console.Write($"\r  files: {scanner.FilesSeen:N0}  written: {writer.RowsWritten:N0}  " +
                          $"dirs skipped: {scanner.DirectoriesSkipped:N0}  hash errors: {scanner.HashErrors:N0}   ");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    /// <summary>Print the final tally once the scan has finished (or been canceled).</summary>
    public void PrintSummary(FileScanner scanner, DatabaseWriter writer, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("Done.");
        Console.WriteLine($"  Files seen:       {scanner.FilesSeen:N0}");
        Console.WriteLine($"  Rows written:     {writer.RowsWritten:N0}");
        string skipNote = scanner.DirectoriesSkipped > 0 ? $"  (logged to {_options.SkipTableName})" : "";
        Console.WriteLine($"  Directories skipped (access): {scanner.DirectoriesSkipped:N0}{skipNote}");
        Console.WriteLine($"  Hash errors:      {scanner.HashErrors:N0}");
        Console.WriteLine($"  Elapsed:          {elapsed:hh\\:mm\\:ss}");
    }

    /// <summary>Print the ranked duplicate sets produced by <see cref="DuplicateAnalyzer"/>.</summary>
    public void PrintDuplicates(DuplicateAnalysis analysis, int topN)
    {
        if (analysis.Scans.Count == 0)
        {
            Console.WriteLine($"No completed scan data found in {_options.TableName}. Run a scan first.");
            return;
        }

        if (analysis.Scans.Count == 1)
        {
            ScanRef s = analysis.Scans[0];
            Console.WriteLine($"Analyzing latest scan for {s.Drive} — scan {s.ScanRunId} (scanned {s.CompletedAtUtc:yyyy-MM-dd HH:mm} UTC).");
        }
        else
        {
            Console.WriteLine($"Analyzing the latest completed scan of {analysis.Scans.Count} drive(s), combined:");
            foreach (ScanRef s in analysis.Scans)
                Console.WriteLine($"  {s.Drive,-10} scan {s.ScanRunId} (scanned {s.CompletedAtUtc:yyyy-MM-dd HH:mm} UTC)");
        }
        Console.WriteLine();

        IReadOnlyList<DuplicateGroup> groups = analysis.Groups;
        if (groups.Count == 0)
        {
            Console.WriteLine("No duplicates found — no hashed content appears at more than one location.");
            return;
        }

        Console.WriteLine($"Total wasted space across all duplicate set(s): {FormatBytes(analysis.TotalWastedBytes)}");
        Console.WriteLine();
        Console.WriteLine($"Top {Math.Min(topN, groups.Count)} duplicate set(s) by wasted space (redundant copies × size):");
        Console.WriteLine();

        int rank = 1;
        foreach (DuplicateGroup g in groups)
        {
            string name = g.DistinctNameCount == 1 ? g.FileName : "<filenames differ>";
            Console.WriteLine(
                $"#{rank,-2} {FormatBytes(g.WastedBytes),11} wasted  |  {name}  |  " +
                $"{g.CopyCount} copies × {FormatBytes(g.SizeBytes)}  |  hash {g.ContentHash[..12]}…");

            foreach (string path in g.SamplePaths)
                Console.WriteLine($"       {path}");
            if (g.CopyCount > g.SamplePaths.Count)
                Console.WriteLine($"       … and {g.CopyCount - g.SamplePaths.Count} more location(s)");

            Console.WriteLine();
            rank++;
        }
    }

    public void ReportFatalError(string message) =>
        Console.Error.WriteLine("\nFatal error during scan: " + message);

    /// <summary>Render a byte count as a human-friendly size (e.g. "1.50 GB").</summary>
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

    /// <summary>Hide any password in the connection string before echoing it to the console.</summary>
    private static string Redact(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string key = parts[i].Split('=', 2)[0].Trim().ToLowerInvariant();
            if (key is "password" or "pwd")
                parts[i] = parts[i].Split('=', 2)[0] + "=***";
        }
        return string.Join(';', parts);
    }
}
