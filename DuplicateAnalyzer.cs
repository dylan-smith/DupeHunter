using Microsoft.Data.SqlClient;

namespace HarddriveDeduper;

/// <summary>A set of identical files — same content hash and size — found at two or more locations.</summary>
public sealed class DuplicateGroup
{
    public required string ContentHash { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>One file name from the group (meaningful only when <see cref="DistinctNameCount"/> is 1).</summary>
    public required string FileName { get; init; }

    /// <summary>How many distinct file names the copies use; &gt; 1 means the name varies across locations.</summary>
    public required int DistinctNameCount { get; init; }

    /// <summary>Number of distinct locations this content lives at.</summary>
    public required int CopyCount { get; init; }

    /// <summary>Reclaimable space: the redundant copies (count − 1) × file size.</summary>
    public required long WastedBytes { get; init; }

    /// <summary>A handful of the locations, for display. May be fewer than <see cref="CopyCount"/>.</summary>
    public List<string> SamplePaths { get; } = new();
}

/// <summary>One drive's scan run that fed an analysis: its drive root, scan id and completion time.</summary>
public sealed record ScanRef(string Drive, string ScanRunId, DateTime CompletedAtUtc);

/// <summary>
/// The outcome of a duplicate analysis: the duplicate sets plus the per-drive scan runs they were
/// combined from. <see cref="Scans"/> is empty when no completed scans exist.
/// </summary>
/// <param name="TotalWastedBytes">
/// Reclaimable space across <em>every</em> duplicate set in the combined runs, not just the ones in
/// <see cref="Groups"/>. This is the grand total the user could recover by deduplicating.
/// </param>
public sealed record DuplicateAnalysis(
    IReadOnlyList<ScanRef> Scans,
    long TotalWastedBytes,
    IReadOnlyList<DuplicateGroup> Groups);

/// <summary>
/// Queries the scanned file table for content that exists in multiple locations and ranks the worst
/// offenders by wasted space. The most recent <em>completed</em> scan run for <em>each drive</em> is
/// selected (per the scan log) and the runs are combined, so duplicates are detected across drives.
/// When <c>--drives</c> is given, only those drives' latest scans are combined. Files present in an
/// earlier scan of a drive but since deleted are excluded (an older run is superseded), and partial
/// data from a scan that never finished is never analyzed. Files are considered identical when their
/// content hash and size both match; rows without a hash are ignored.
/// </summary>
public sealed class DuplicateAnalyzer
{
    private readonly Options _options;

    public DuplicateAnalyzer(Options options) => _options = options;

    /// <summary>
    /// Find the <paramref name="topN"/> duplicate sets with the most wasted space across the latest
    /// completed scan of each drive (filtered by <c>--drives</c> when given), attaching up to
    /// <paramref name="samplePathsPerGroup"/> example locations to each.
    /// </summary>
    public async Task<DuplicateAnalysis> FindTopDuplicatesAsync(
        int topN, int samplePathsPerGroup, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        List<ScanRef> scans = await GetLatestCompletedScansAsync(conn, ct);
        if (scans.Count == 0)
        {
            string scope = _options.Drives.Count > 0
                ? $" for the selected drive(s): {string.Join(", ", _options.Drives)}"
                : "";
            throw new InvalidOperationException(
                $"No completed scan found in '{_options.ScanTableName}'{scope}. Run a scan to completion before analyzing " +
                "(scans that were canceled, failed, or never finished are not eligible for analysis).");
        }

        var runIds = scans.Select(s => s.ScanRunId).ToList();
        long totalWasted = await QueryTotalWastedAsync(conn, runIds, ct);
        List<DuplicateGroup> groups = await QueryTopGroupsAsync(conn, runIds, topN, ct);

        foreach (DuplicateGroup g in groups)
            await LoadSamplePathsAsync(conn, runIds, g, samplePathsPerGroup, ct);

        return new DuplicateAnalysis(scans, totalWasted, groups);
    }

    /// <summary>
    /// The runs to analyze: the most recent <em>completed</em> scan for each drive, or an empty list
    /// if none exist. When <c>--drives</c> is given, only those drives are considered. Only completed
    /// runs are eligible — partial data from a canceled, failed, or never-finished scan is never analyzed.
    /// </summary>
    private async Task<List<ScanRef>> GetLatestCompletedScansAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();

        // Optionally restrict to the drives the user named on the command line.
        string driveFilter = "";
        if (_options.Drives.Count > 0)
        {
            var names = new string[_options.Drives.Count];
            for (int i = 0; i < _options.Drives.Count; i++)
            {
                names[i] = "@drive" + i;
                cmd.Parameters.AddWithValue("@drive" + i, _options.Drives[i]);
            }
            driveFilter = " AND Drive IN (" + string.Join(", ", names) + ")";
        }

        // One row per drive — the newest completed run (ROW_NUMBER breaks any same-timestamp tie so a
        // drive never contributes two runs, which would double-count files). Guard against the log table
        // not existing (older databases / analyze-only runs).
        cmd.CommandText = $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NOT NULL
    SELECT Drive, ScanRunId, CompletedAtUtc
    FROM (
        SELECT Drive, ScanRunId, CompletedAtUtc,
               ROW_NUMBER() OVER (PARTITION BY Drive ORDER BY CompletedAtUtc DESC, ScanRunId) AS rn
        FROM {_options.ScanTableName}
        WHERE Status = 'Completed' AND Drive IS NOT NULL{driveFilter}
    ) ranked
    WHERE rn = 1
    ORDER BY Drive;";
        cmd.CommandTimeout = 0;

        var scans = new List<ScanRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            scans.Add(new ScanRef(reader.GetString(0), reader.GetString(1).TrimEnd(), reader.GetDateTime(2)));

        return scans;
    }

    /// <summary>
    /// Sum the reclaimable space over <em>all</em> duplicate sets across the combined runs — every set
    /// of identical content contributes (copies − 1) × size. This is the total before the top-N cut.
    /// </summary>
    private async Task<long> QueryTotalWastedAsync(SqlConnection conn, IReadOnlyList<string> runIds, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT COALESCE(SUM(WastedBytes), 0)
FROM (
    SELECT CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes AS WastedBytes
    FROM {_options.TableName}
    WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL
    GROUP BY ContentHash, SizeBytes
    HAVING COUNT(*) > 1
) AS perGroup;";
        cmd.CommandTimeout = 0;

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long total ? total : Convert.ToInt64(result);
    }

    private async Task<List<DuplicateGroup>> QueryTopGroupsAsync(
        SqlConnection conn, IReadOnlyList<string> runIds, int topN, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT TOP (@topN)
    ContentHash,
    SizeBytes,
    COUNT(*)                                  AS CopyCount,
    CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes  AS WastedBytes,
    MIN(FileName)                             AS SampleName,
    COUNT(DISTINCT FileName)                  AS DistinctNameCount
FROM {_options.TableName}
WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL
GROUP BY ContentHash, SizeBytes
HAVING COUNT(*) > 1
ORDER BY WastedBytes DESC;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@topN", topN);

        var groups = new List<DuplicateGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new DuplicateGroup
            {
                ContentHash = reader.GetString(0).TrimEnd(),
                SizeBytes = reader.GetInt64(1),
                CopyCount = reader.GetInt32(2),
                WastedBytes = reader.GetInt64(3),
                FileName = reader.GetString(4),
                DistinctNameCount = reader.GetInt32(5),
            });
        }
        return groups;
    }

    private async Task LoadSamplePathsAsync(
        SqlConnection conn, IReadOnlyList<string> runIds, DuplicateGroup g, int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT TOP (@limit) FullPath
FROM {_options.TableName}
WHERE ScanRunId IN ({inClause}) AND ContentHash = @hash AND SizeBytes = @size
ORDER BY FullPath;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@hash", g.ContentHash);
        cmd.Parameters.AddWithValue("@size", g.SizeBytes);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            g.SamplePaths.Add(reader.GetString(0));
    }

    /// <summary>
    /// Add a <c>@run{i}</c> parameter for each scan id and return the comma-separated parameter list
    /// for use inside a <c>ScanRunId IN (...)</c> clause.
    /// </summary>
    private static string BuildRunIdInClause(SqlCommand cmd, IReadOnlyList<string> runIds)
    {
        var names = new string[runIds.Count];
        for (int i = 0; i < runIds.Count; i++)
        {
            names[i] = "@run" + i;
            cmd.Parameters.AddWithValue("@run" + i, runIds[i]);
        }
        return string.Join(", ", names);
    }
}
