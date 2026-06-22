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
        Console.WriteLine($"  Directories skipped (access): {scanner.DirectoriesSkipped:N0}");
        Console.WriteLine($"  Hash errors:      {scanner.HashErrors:N0}");
        Console.WriteLine($"  Elapsed:          {elapsed:hh\\:mm\\:ss}");
    }

    public void ReportFatalError(string message) =>
        Console.Error.WriteLine("\nFatal error during scan: " + message);

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
