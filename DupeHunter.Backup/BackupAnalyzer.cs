using Microsoft.Data.Sqlite;

namespace DupeHunter.Backup;

/// <summary>
/// Checks a backup folder against the scan database: which of its files/folders already exist outside
/// the backup tree (and can therefore be deleted from the backup), per the latest completed scan of
/// each drive. Reads only the database — no disk contents are read or verified.
/// </summary>
/// <remarks>
/// Memory scales with the backup subtree plus the external copies of the backup's content (capped per
/// content by <see cref="BackupOptions.MaxCopiesPerKey"/>) — comfortable up to backups of roughly a
/// million files. If that ever becomes a limit, the fallback is analyzing one top-level folder at a time.
/// </remarks>
public sealed class BackupAnalyzer
{
    private readonly BackupOptions _options;

    public BackupAnalyzer(BackupOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Run the full analysis and return the report (deletable / unmatched / unverifiable entries).</summary>
    public async Task<BackupReport> AnalyzeAsync(CancellationToken ct, IStepProgress? progress = null)
    {
        var backupRoot = _options.BackupRoot
            ?? throw new InvalidOperationException("No backup folder was given.");

        // One connection for the whole analysis: the temp tables built below are per-connection.
        await using var conn = await Database.OpenConnectionAsync(_options.ToCoreOptions(), ct);

        progress?.BeginStep("Selecting the latest completed scan per drive…");
        var scans = await ScanSelector.GetLatestCompletedScansAsync(conn, _options.ScanTableName, _options.Drives, ct);
        var backupScan = ResolveBackupScan(scans, backupRoot);

        progress?.BeginStep("Loading the backup tree…");
        var rows = await LoadBackupRowsAsync(conn, backupScan.ScanRunId, backupRoot, progress, ct);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                $"No scanned files were found under '{backupRoot}' in the latest completed scan of {backupScan.Drive} " +
                $"({backupScan.CompletedAtUtc:u}). Check the path — or the folder is empty, or was skipped during " +
                "the scan (see the skip table).");
        }

        var tree = BackupTreeBuilder.Build(backupRoot, rows);

        progress?.BeginStep("Finding external copies…");
        var index = await BuildExternalIndexAsync(conn, scans, rows, backupRoot, progress, ct);

        progress?.BeginStep("Matching folders against external content…");
        var report = new BackupReport { GeneratedUtc = DateTime.UtcNow, BackupRoot = backupRoot };
        foreach (var e in _options.Excludes)
        {
            report.Excludes.Add(e);
        }

        foreach (var s in scans)
        {
            report.Scans.Add(s);
        }

        new BackupMatcher(index, _options.MaxLocations, report).Run(tree);
        return report;
    }

    /// <summary>
    /// The scan run covering the backup folder: the selected scan whose drive root is the longest
    /// case-insensitive prefix of the backup path. Throws with a pointed message when nothing covers it.
    /// </summary>
    private ScanRef ResolveBackupScan(IReadOnlyList<ScanRef> scans, string backupRoot)
    {
        if (scans.Count == 0)
        {
            var scope = _options.Drives.Count > 0
                ? $" for the selected drive(s): {string.Join(", ", _options.Drives)}"
                : "";
            throw new InvalidOperationException(
                $"No completed scan found in '{_options.ScanTableName}'{scope}. Run dupehunter to completion first " +
                "(scans that were canceled, failed, or never finished are not eligible).");
        }

        ScanRef? best = null;
        foreach (var s in scans)
        {
            if (IsUnderOrEqual(backupRoot, s.Drive) && (best is null || s.Drive.Length > best.Drive.Length))
            {
                best = s;
            }
        }

        if (best is null)
        {
            var drivesNote = _options.Drives.Count > 0
                ? " Note: --drives must include the backup folder's own drive."
                : "";
            throw new InvalidOperationException(
                $"Backup folder '{backupRoot}' is not covered by any completed scan. Completed scans exist for: " +
                $"{string.Join(", ", scans.Select(s => s.Drive))}.{drivesNote}");
        }

        return best;
    }

    /// <summary>
    /// Every file and folder row under the backup root from the backup drive's run. There is no index on
    /// FullPath, so this streams the run's rows and filters by path prefix client-side (LIKE is avoided
    /// deliberately: '_' wildcards in path prefixes cause false positives, and range tricks break on
    /// casing differences between the user's input and the stored paths).
    /// </summary>
    private async Task<List<BackupRow>> LoadBackupRowsAsync(
        SqliteConnection conn, string runId, string backupRoot, IStepProgress? progress, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT EntryType, FullPath, SizeBytes, ContentHash, ScanError
FROM {_options.TableName}
WHERE ScanRunId = @run AND EntryType IN ('F', 'D');";
        cmd.Parameters.AddWithValue("@run", runId);
        cmd.CommandTimeout = 0;

        var rows = new List<BackupRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fullPath = reader.GetString(1);
            if (!IsUnderOrEqual(fullPath, backupRoot))
            {
                continue;
            }

            rows.Add(new BackupRow(
                IsFolder: reader.GetString(0) == "D",
                FullPath: fullPath,
                SizeBytes: reader.GetInt64(2),
                ContentHash: reader.IsDBNull(3) ? null : reader.GetString(3).TrimEnd(),
                ScanError: reader.IsDBNull(4) ? null : reader.GetString(4)));

            if (rows.Count % 10_000 == 0)
            {
                progress?.UpdateStep($"Loading the backup tree… ({rows.Count:N0} entries)");
            }
        }

        return rows;
    }

    /// <summary>
    /// Find every external copy of the backup's content in one pass: the backup's distinct file contents
    /// and folder fingerprints go into per-connection temp tables, which are then joined against the
    /// file table's duplicate index — one indexed seek per distinct content instead of a table scan per
    /// folder. Paths inside the backup tree or an excluded folder are dropped here, so the index only
    /// ever answers with genuinely external locations.
    /// </summary>
    private async Task<ExternalCopyIndex> BuildExternalIndexAsync(
        SqliteConnection conn, IReadOnlyList<ScanRef> scans, IReadOnlyList<BackupRow> rows,
        string backupRoot, IStepProgress? progress, CancellationToken ct)
    {
        var fileKeys = new HashSet<ContentKey>();
        var folderFps = new HashSet<ContentKey>();
        foreach (var row in rows)
        {
            if (row.ContentHash is null)
            {
                continue;
            }

            (row.IsFolder ? folderFps : fileKeys).Add(new ContentKey(row.ContentHash, row.SizeBytes));
        }

        await CreateAndFillTempTableAsync(conn, "BackupFileKeys", fileKeys, ct);
        await CreateAndFillTempTableAsync(conn, "BackupFolderFps", folderFps, ct);

        var index = new ExternalCopyIndex(_options.MaxCopiesPerKey);
        var runIds = scans.Select(s => s.ScanRunId).ToList();

        var copies = 0;
        await StreamMatchesAsync(conn, "BackupFileKeys", "F", runIds, (key, path) =>
        {
            if (IsExternal(path, backupRoot))
            {
                index.AddFileCopy(key, path);
                if (++copies % 50_000 == 0)
                {
                    progress?.UpdateStep($"Finding external copies… ({copies:N0} found)");
                }
            }
        }, ct);

        if (folderFps.Count > 0)
        {
            await StreamMatchesAsync(conn, "BackupFolderFps", "D", runIds, (key, path) =>
            {
                // An exact-fingerprint folder vouches with its *own* content, so beyond being external it
                // must not contain the backup tree or an excluded folder (its fingerprint would then be
                // built partly from the very content it is supposed to duplicate).
                if (IsExternal(path, backupRoot)
                    && !IsUnderOrEqual(backupRoot, path)
                    && !_options.Excludes.Any(e => IsUnderOrEqual(e, path)))
                {
                    index.AddFolderMatch(key, path);
                }
            }, ct);
        }

        index.SortForSearch();
        return index;
    }

    /// <summary>Create a temp table keyed by (hash, size) and fill it with the given contents.</summary>
    private static async Task CreateAndFillTempTableAsync(
        SqliteConnection conn, string name, IReadOnlyCollection<ContentKey> keys, CancellationToken ct)
    {
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = $@"
CREATE TEMP TABLE {name} (
    Hash TEXT    NOT NULL,
    Size INTEGER NOT NULL,
    PRIMARY KEY (Hash, Size)
) WITHOUT ROWID;";
            await create.ExecuteNonQueryAsync(ct);
        }

        if (keys.Count == 0)
        {
            return;
        }

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"INSERT INTO temp.{name} (Hash, Size) VALUES (@h, @s);";
        var hash = insert.Parameters.Add("@h", SqliteType.Text);
        var size = insert.Parameters.Add("@s", SqliteType.Integer);

        foreach (var key in keys)
        {
            hash.Value = key.Hash;
            size.Value = key.SizeBytes;
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Stream every row of the selected runs whose (hash, size) appears in the temp table. CROSS JOIN
    /// pins the join order — outer scan of the small temp table, inner seek on the duplicate index — so
    /// the file table is never scanned.
    /// </summary>
    private async Task StreamMatchesAsync(
        SqliteConnection conn, string tempTable, string entryType, IReadOnlyList<string> runIds,
        Action<ContentKey, string> onMatch, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var names = new string[runIds.Count];
        for (var i = 0; i < runIds.Count; i++)
        {
            names[i] = "@run" + i;
            cmd.Parameters.AddWithValue(names[i], runIds[i]);
        }

        cmd.CommandText = $@"
SELECT k.Hash, k.Size, f.FullPath
FROM temp.{tempTable} k
CROSS JOIN {_options.TableName} f
    ON f.EntryType = '{entryType}' AND f.ContentHash = k.Hash AND f.SizeBytes = k.Size
WHERE f.ScanRunId IN ({string.Join(", ", names)});";
        cmd.CommandTimeout = 0;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            onMatch(new ContentKey(reader.GetString(0), reader.GetInt64(1)), reader.GetString(2));
        }
    }

    /// <summary>True when the path lies outside the backup tree and outside every excluded folder.</summary>
    private bool IsExternal(string path, string backupRoot) =>
        !IsUnderOrEqual(path, backupRoot) && !_options.Excludes.Any(e => IsUnderOrEqual(path, e));

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies underneath it.</summary>
    internal static bool IsUnderOrEqual(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(WithSeparator(root), StringComparison.OrdinalIgnoreCase);

    /// <summary>The root as a path prefix ending in a separator (a drive root already carries one).</summary>
    internal static string WithSeparator(string root) => root.EndsWith('\\') ? root : root + "\\";
}
