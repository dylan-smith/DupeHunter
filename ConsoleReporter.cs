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

    /// <summary>Print a header line naming the drive about to be scanned, e.g. "[1/3] C:\".</summary>
    public void PrintDriveHeader(int index, int total, string drive) =>
        Console.WriteLine($"[{index}/{total}] {drive}");

    /// <summary>
    /// Start an in-place progress line for a single pass of the current drive. The line is prefixed
    /// with <paramref name="label"/> and repainted once a second from <paramref name="snapshot"/>.
    /// Dispose (await) the returned handle to repaint the final values, finalize the line with a
    /// newline, and stop the timer — so each pass of each drive ends up on its own line.
    /// </summary>
    public IAsyncDisposable StartPass(string label, Func<string> snapshot) =>
        new PassProgress(label, snapshot);

    /// <summary>
    /// Render a fixed-width text progress bar like <c>[#########-----------]  45%</c> for a pass whose
    /// total is known up front. A non-positive <paramref name="total"/> renders as complete.
    /// </summary>
    public static string ProgressBar(long done, long total, int width = 20)
    {
        double fraction = total <= 0 ? 1.0 : Math.Clamp((double)done / total, 0.0, 1.0);
        int filled = (int)Math.Round(fraction * width);
        return $"[{new string('#', filled)}{new string('-', width - filled)}] {fraction * 100,3:0}%";
    }

    /// <summary>One drive-pass's live progress line: repaints in place, then commits its own line.</summary>
    private sealed class PassProgress : IAsyncDisposable
    {
        private readonly string _label;
        private readonly Func<string> _snapshot;
        private readonly Timer _timer;

        public PassProgress(string label, Func<string> snapshot)
        {
            _label = label;
            _snapshot = snapshot;
            Paint();
            _timer = new Timer(_ => Paint(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        // Trailing spaces clear any leftover characters from a previously longer line.
        private void Paint() => Console.Write($"\r    {_label}: {_snapshot()}   ");

        public async ValueTask DisposeAsync()
        {
            // Awaiting disposal guarantees no repaint is in flight before we print the final values.
            await _timer.DisposeAsync();
            Paint();
            Console.WriteLine();
        }
    }

    /// <summary>Print the final tally once the scan has finished (or been canceled).</summary>
    public void PrintSummary(FileScanner scanner, DatabaseWriter writer, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("Done.");
        Console.WriteLine($"  Files seen:       {scanner.FilesSeen:N0}");
        Console.WriteLine($"  Rows written:     {writer.RowsWritten:N0}");
        Console.WriteLine($"  Files hashed:     {scanner.FilesHashed:N0}");
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
