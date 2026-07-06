using Microsoft.Data.Sqlite;

namespace DupeHunter;

/// <summary>
/// Selects the scan runs an analysis should read: the most recent <em>completed</em> scan per drive.
/// Shared by <see cref="DuplicateAnalyzer"/>, <see cref="DatabaseCleaner"/> and the backup checker so
/// they all agree on which runs represent the current state of each drive.
/// </summary>
public static class ScanSelector
{
    /// <summary>
    /// The most recent completed scan for each drive, or an empty list if the scan log doesn't exist or
    /// no run ever completed. Only completed runs are eligible — partial data from a canceled, failed,
    /// or never-finished scan is never analyzed. <paramref name="drives"/> restricts the result to those
    /// drive roots (empty = all drives).
    /// </summary>
    public static async Task<List<ScanRef>> GetLatestCompletedScansAsync(
        SqliteConnection conn, string scanTableName, IReadOnlyList<string> drives, CancellationToken ct)
    {
        if (conn is null)
        {
            throw new ArgumentNullException(nameof(conn));
        }

        if (drives is null)
        {
            throw new ArgumentNullException(nameof(drives));
        }

        // Guard against the log table not existing (fresh database / analyze-only on an unscanned file).
        if (!await TableExistsAsync(conn, scanTableName, ct))
        {
            return [];
        }

        await using var cmd = conn.CreateCommand();

        // Optionally restrict to the drives the caller named.
        var driveFilter = "";
        if (drives.Count > 0)
        {
            var names = new string[drives.Count];
            for (var i = 0; i < drives.Count; i++)
            {
                names[i] = "@drive" + i;
                cmd.Parameters.AddWithValue("@drive" + i, drives[i]);
            }
            driveFilter = " AND Drive IN (" + string.Join(", ", names) + ")";
        }

        // One row per drive — the newest completed run (ROW_NUMBER breaks any same-timestamp tie so a
        // drive never contributes two runs, which would double-count files).
        cmd.CommandText = $@"
SELECT Drive, ScanRunId, CompletedAtUtc
FROM (
    SELECT Drive, ScanRunId, CompletedAtUtc,
           ROW_NUMBER() OVER (PARTITION BY Drive ORDER BY CompletedAtUtc DESC, ScanRunId) AS rn
    FROM {scanTableName}
    WHERE Status = 'Completed' AND Drive IS NOT NULL{driveFilter}
) ranked
WHERE rn = 1
ORDER BY Drive;";
        cmd.CommandTimeout = 0;

        var scans = new List<ScanRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scans.Add(new ScanRef(reader.GetString(0), reader.GetString(1).TrimEnd(), reader.GetDateTime(2)));
        }

        return scans;
    }

    /// <summary>True if a table of the given name exists in the database.</summary>
    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t;";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
}
