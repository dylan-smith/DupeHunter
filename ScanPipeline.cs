using System.Collections.Concurrent;

namespace HarddriveDeduper;

/// <summary>
/// Runs the scan in two passes per drive, each drive being its own scan run with a distinct
/// ScanRunId. Pass one enumerates every file and writes its metadata (with no content hash). Pass
/// two reads those rows back and fills in their content hashes. Committing the full metadata
/// inventory before any hashing means an interrupted hashing pass still leaves a complete file
/// listing behind. Drives are scanned one after another. Returns a process exit code
/// (0 = success, 130 = canceled, 1 = fatal error).
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

        try
        {
            // Pass one: enumerate and persist metadata for every file.
            await EnumerateMetadataAsync(root, ct);

            // Pass two: read the rows back and fill in their content hashes.
            if (_options.ComputeHash)
                await ComputeHashesAsync(ct);

            await _writer.WriteSkipsAsync(DrainSkips(), CancellationToken.None);
            await _writer.CompleteScanAsync("Completed", null, CancellationToken.None);
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Flush whatever metadata was already buffered before exiting.
            try { await _writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
            try { await _writer.WriteSkipsAsync(DrainSkips(), CancellationToken.None); } catch { /* ignore */ }
            // Record the partial run as canceled; its rows must not be treated as a complete inventory.
            try { await _writer.CompleteScanAsync("Canceled", null, CancellationToken.None); } catch { /* ignore */ }
            return 130;
        }
        catch (Exception ex)
        {
            _reporter.ReportFatalError(ex.Message);
            try { await _writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
            try { await _writer.CompleteScanAsync("Failed", ex.Message, CancellationToken.None); } catch { /* ignore */ }
            return 1;
        }
    }

    /// <summary>
    /// Pass one: walk the tree and bulk-insert each file's metadata. Enumeration is single-threaded
    /// (it's directory I/O, not CPU work) and <see cref="DatabaseWriter.AddAsync"/> flushes in batches.
    /// </summary>
    private async Task EnumerateMetadataAsync(string root, CancellationToken ct)
    {
        foreach (FileRecord record in _scanner.EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            await _writer.AddAsync(record, ct);
        }

        await _writer.FlushAsync(ct);
    }

    /// <summary>
    /// Pass two: page through this run's rows (by Id) and hash each file on N threads, writing the
    /// results back in batches. Each chunk is read fully, hashed, then updated before the next chunk
    /// is read, so reads and updates never contend on the connection and memory stays bounded.
    /// </summary>
    private async Task ComputeHashesAsync(CancellationToken ct)
    {
        await _writer.BeginHashPassAsync(ct);

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.Parallelism,
            CancellationToken = ct,
        };

        long afterId = 0;
        while (true)
        {
            IReadOnlyList<PendingHash> chunk = await _writer.ReadNextHashChunkAsync(afterId, ct);
            if (chunk.Count == 0)
                break;

            // Rows come back ordered by Id, so the last one is this chunk's high-water mark.
            afterId = chunk[^1].Id;

            // Hash the chunk in parallel; keep only rows that actually got a hash or hit an error
            // (files skipped for exceeding the size limit are left with their NULL hash).
            var updates = new ConcurrentQueue<HashResult>();
            await Parallel.ForEachAsync(chunk, parallelOpts, (pending, _) =>
            {
                HashResult result = _scanner.HashFile(pending);
                if (result.ContentHash is not null || result.Error is not null)
                    updates.Enqueue(result);
                return ValueTask.CompletedTask;
            });

            await _writer.UpdateHashesAsync(updates, ct);
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
