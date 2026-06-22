using System.Threading.Channels;

namespace HarddriveDeduper;

/// <summary>
/// Runs the scan as a producer/consumer pipeline. Each drive is scanned as its own scan run with a
/// distinct ScanRunId: many threads enumerate and hash that drive's files into a bounded channel,
/// while a single consumer drains the channel into SQL Server. Drives are scanned one after another.
/// Returns a process exit code (0 = success, 130 = canceled, 1 = fatal error).
/// </summary>
public sealed class ScanPipeline
{
    private readonly Options _options;
    private readonly FileScanner _scanner;
    private readonly DatabaseWriter _writer;
    private readonly ConsoleReporter _reporter;

    public ScanPipeline(Options options, FileScanner scanner, DatabaseWriter writer, ConsoleReporter reporter)
    {
        _options = options;
        _scanner = scanner;
        _writer = writer;
        _reporter = reporter;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        await using IAsyncDisposable progress = _reporter.StartProgress(_scanner, _writer);

        // Each drive is a separate scan run. Scan them in turn; stop early if one is canceled or fails
        // so a partial inventory is never silently followed by more drives.
        foreach (string root in roots)
        {
            int code = await ScanDriveAsync(root, ct);
            if (code != 0)
                return code;
        }

        return 0;
    }

    /// <summary>Scan a single drive root as its own scan run, returning a process exit code.</summary>
    private async Task<int> ScanDriveAsync(string root, CancellationToken ct)
    {
        // Log this drive's run as started; it stays "Running" with no completion time until we stamp it below.
        await _writer.BeginScanAsync(root, ct);

        // Give producers enough headroom to keep hashing while the consumer is mid-flush.
        const int batchHeadroomFactor = 2;
        var channel = Channel.CreateBounded<FileRecord>(new BoundedChannelOptions(_options.BatchSize * batchHeadroomFactor)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Single consumer: write each record to SQL as it arrives.
        Task consumer = Task.Run(async () =>
        {
            await foreach (FileRecord rec in channel.Reader.ReadAllAsync(ct))
                await _writer.AddAsync(rec, ct);
        });

        try
        {
            // Hash on N threads when hashing is on; otherwise a single producer is enough.
            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.ComputeHash ? _options.Parallelism : 1,
                CancellationToken = ct,
            };

            await Parallel.ForEachAsync(_scanner.EnumerateFiles(root), parallelOpts, async (record, token) =>
            {
                _scanner.ComputeHash(record);
                await channel.Writer.WriteAsync(record, token);
            });

            channel.Writer.Complete();
            await consumer;
            await _writer.FlushAsync(CancellationToken.None);
            await _writer.WriteSkipsAsync(DrainSkips(), CancellationToken.None);
            await _writer.CompleteScanAsync("Completed", null, CancellationToken.None);
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Drain and flush whatever was already buffered before exiting.
            channel.Writer.TryComplete();
            try { await consumer; } catch { /* ignore */ }
            try { await _writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
            try { await _writer.WriteSkipsAsync(DrainSkips(), CancellationToken.None); } catch { /* ignore */ }
            // Record the partial run as canceled; its rows must not be treated as a complete inventory.
            try { await _writer.CompleteScanAsync("Canceled", null, CancellationToken.None); } catch { /* ignore */ }
            return 130;
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            _reporter.ReportFatalError(ex.Message);
            try { await _writer.CompleteScanAsync("Failed", ex.Message, CancellationToken.None); } catch { /* ignore */ }
            return 1;
        }
    }

    /// <summary>
    /// Remove and return the directory skips accumulated so far. Called once per drive after its
    /// enumeration finishes, so the returned skips belong to the drive just scanned and are tagged
    /// with that drive's ScanRunId by the writer.
    /// </summary>
    private SkipRecord[] DrainSkips()
    {
        var skips = new List<SkipRecord>();
        while (_scanner.Skips.TryDequeue(out SkipRecord? skip))
            skips.Add(skip);
        return skips.ToArray();
    }
}
