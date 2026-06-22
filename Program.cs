using System.Diagnostics;
using System.Threading.Channels;
using HarddriveDeduper;

Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.HelpText());
    return 0;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Warning: this tool targets Windows drive semantics; behavior on other platforms is best-effort.");
}

// Resolve the drives to scan.
List<string> roots = ResolveRoots(options);
if (roots.Count == 0)
{
    Console.Error.WriteLine("No drives to scan.");
    return 1;
}

Console.WriteLine($"Scanning {roots.Count} drive(s): {string.Join(", ", roots)}");
Console.WriteLine($"Hashing: {(options.ComputeHash ? $"SHA-256 ({options.Parallelism} threads)" : "disabled")}");
Console.WriteLine($"Database: {Redact(options.ConnectionString)}  ->  {options.TableName}");
Console.WriteLine();

// Ctrl-C => graceful flush & exit.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCancellation requested — flushing buffered rows...");
    cts.Cancel();
};

var scanner = new FileScanner(options);
using var writer = new DatabaseWriter(options);

try
{
    await writer.InitializeAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to connect / initialize database:");
    Console.Error.WriteLine("  " + ex.Message);
    return 3;
}

var sw = Stopwatch.StartNew();

// Pipeline: producer enumerates + hashes in parallel, single consumer writes to SQL.
var channel = Channel.CreateBounded<FileRecord>(new BoundedChannelOptions(options.BatchSize * 2)
{
    SingleReader = true,
    SingleWriter = false,
    FullMode = BoundedChannelFullMode.Wait,
});

Task consumer = Task.Run(async () =>
{
    await foreach (FileRecord rec in channel.Reader.ReadAllAsync(cts.Token))
        await writer.AddAsync(rec, cts.Token);
});

// Periodic progress line.
using var progress = new Timer(_ =>
{
    Console.Write($"\r  files: {scanner.FilesSeen:N0}  written: {writer.RowsWritten:N0}  " +
                  $"dirs skipped: {scanner.DirectoriesSkipped:N0}  hash errors: {scanner.HashErrors:N0}   ");
}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

int exitCode = 0;
try
{
    var parallelOpts = new ParallelOptions
    {
        MaxDegreeOfParallelism = options.ComputeHash ? options.Parallelism : 1,
        CancellationToken = cts.Token,
    };

    IEnumerable<FileRecord> allFiles = roots.SelectMany(scanner.EnumerateFiles);

    await Parallel.ForEachAsync(allFiles, parallelOpts, async (record, ct) =>
    {
        scanner.ComputeHash(record);
        await channel.Writer.WriteAsync(record, ct);
    });

    channel.Writer.Complete();
    await consumer;
    await writer.FlushAsync(CancellationToken.None);
}
catch (OperationCanceledException)
{
    channel.Writer.TryComplete();
    try { await consumer; } catch { /* ignore */ }
    try { await writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
    exitCode = 130; // canceled
}
catch (Exception ex)
{
    channel.Writer.TryComplete(ex);
    Console.Error.WriteLine("\nFatal error during scan: " + ex.Message);
    exitCode = 1;
}
finally
{
    await progress.DisposeAsync();
}

sw.Stop();
Console.WriteLine();
Console.WriteLine();
Console.WriteLine("Done.");
Console.WriteLine($"  Files seen:       {scanner.FilesSeen:N0}");
Console.WriteLine($"  Rows written:     {writer.RowsWritten:N0}");
Console.WriteLine($"  Directories skipped (access): {scanner.DirectoriesSkipped:N0}");
Console.WriteLine($"  Hash errors:      {scanner.HashErrors:N0}");
Console.WriteLine($"  Elapsed:          {sw.Elapsed:hh\\:mm\\:ss}");
return exitCode;

// --- local helpers -------------------------------------------------------

static List<string> ResolveRoots(Options options)
{
    if (options.Drives.Count > 0)
        return options.Drives;

    // Default: every ready fixed drive.
    return DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => d.RootDirectory.FullName)
        .ToList();
}

static string Redact(string connectionString)
{
    // Hide any password before echoing the connection string.
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < parts.Length; i++)
    {
        string key = parts[i].Split('=', 2)[0].Trim().ToLowerInvariant();
        if (key is "password" or "pwd")
            parts[i] = parts[i].Split('=', 2)[0] + "=***";
    }
    return string.Join(';', parts);
}
