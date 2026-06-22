using System.Data;
using Microsoft.Data.SqlClient;

namespace HarddriveDeduper;

/// <summary>
/// Ensures the destination table exists and streams <see cref="FileRecord"/> rows into it
/// efficiently via <see cref="SqlBulkCopy"/>, flushing in batches.
/// </summary>
public sealed class DatabaseWriter : IDisposable
{
    /// <summary>Max length of the <c>FileName</c> column; longer names are truncated before insert.</summary>
    private const int FileNameMaxLength = 260;

    /// <summary>Creates the target database (named in the connection string) if it doesn't exist.</summary>
    private const string CreateDatabaseIfMissingSql = @"
IF DB_ID(@db) IS NULL
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@db);
    EXEC sp_executesql @sql;
END";

    private readonly Options _options;
    private readonly SqlConnection _connection;
    private readonly DataTable _buffer;

    /// <summary>ScanRunId of the drive currently being scanned. Reset by each <see cref="BeginScanAsync"/>.</summary>
    private string _scanRunId = string.Empty;

    /// <summary>Rows written for the current drive's scan run; reset by each <see cref="BeginScanAsync"/>.</summary>
    private long _rowsWrittenThisScan;

    /// <summary>Total rows written across every drive scanned this process (for the final summary).</summary>
    public long RowsWritten;

    public DatabaseWriter(Options options)
    {
        _options = options;
        _connection = new SqlConnection(options.ConnectionString);
        _buffer = BuildSchemaTable();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureDatabaseExistsAsync(ct);
        await _connection.OpenAsync(ct);

        if (_options.Recreate)
        {
            await ExecuteAsync(DropTableSql(), ct);
            await ExecuteAsync(DropSkipsTableSql(), ct);
        }

        await ExecuteAsync(CreateTableSql(), ct);
        await ExecuteAsync(CreateSkipsTableSql(), ct);

        // The scan log is an audit history across runs, so it is created but never dropped here.
        await ExecuteAsync(CreateScansTableSql(), ct);
        // Bring an older scan log (created before per-drive scans) up to the current schema.
        await ExecuteAsync(MigrateScansTableSql(), ct);
    }

    /// <summary>
    /// Record the start of a scan run for a single <paramref name="drive"/>: generates a fresh
    /// <c>ScanRunId</c> and inserts a row tagged <c>Running</c> with no completion time. Every drive is
    /// its own scan run, so subsequent rows are tagged with this id until the next <see cref="BeginScanAsync"/>.
    /// A row that stays <c>Running</c> (or keeps a NULL <c>CompletedAtUtc</c>) marks a scan that never
    /// finished — e.g. the process was killed — and is excluded from analysis.
    /// </summary>
    public async Task BeginScanAsync(string drive, CancellationToken ct)
    {
        _scanRunId = Guid.NewGuid().ToString("N");
        _rowsWrittenThisScan = 0;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {_options.ScanTableName}
    (ScanRunId, StartedAtUtc, Status, MachineName, Drive, Roots, ComputeHash)
VALUES (@id, @started, @status, @machine, @drive, @roots, @hash);";
        cmd.Parameters.AddWithValue("@id", _scanRunId);
        cmd.Parameters.AddWithValue("@started", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@status", "Running");
        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
        cmd.Parameters.AddWithValue("@drive", drive);
        cmd.Parameters.AddWithValue("@roots", drive);
        cmd.Parameters.AddWithValue("@hash", _options.ComputeHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Stamp this scan run as finished: sets the completion time, final status (e.g. <c>Completed</c>,
    /// <c>Canceled</c>, <c>Failed</c>), the number of file rows written, and any fatal error message.
    /// </summary>
    public async Task CompleteScanAsync(string status, string? error, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
UPDATE {_options.ScanTableName}
SET CompletedAtUtc = @completed,
    Status         = @status,
    FilesWritten   = @files,
    ScanError      = @error
WHERE ScanRunId = @id;";
        cmd.Parameters.AddWithValue("@completed", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@files", _rowsWrittenThisScan);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", _scanRunId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Persist the directories skipped during the scan, tagged with this run's <c>ScanRunId</c> so they
    /// can be correlated with the file rows. One-shot bulk insert; safe to call with an empty set.
    /// </summary>
    public async Task WriteSkipsAsync(IReadOnlyCollection<SkipRecord> skips, CancellationToken ct)
    {
        if (skips.Count == 0)
            return;

        var table = new DataTable();
        table.Columns.Add("FullPath", typeof(string));
        table.Columns.Add("Reason", typeof(string));
        table.Columns.Add("ScanRunId", typeof(string));
        table.Columns.Add("ScannedAtUtc", typeof(DateTime));

        DateTime now = DateTime.UtcNow;
        foreach (SkipRecord s in skips)
        {
            DataRow row = table.NewRow();
            row["FullPath"] = s.FullPath;
            row["Reason"] = s.Reason;
            row["ScanRunId"] = _scanRunId;
            row["ScannedAtUtc"] = now;
            table.Rows.Add(row);
        }

        using var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = _options.SkipTableName,
            BulkCopyTimeout = 0,
            BatchSize = _options.BatchSize,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(table, ct);
    }

    /// <summary>Add a record to the buffer; flushes automatically once the batch size is reached.</summary>
    public async Task AddAsync(FileRecord r, CancellationToken ct)
    {
        DataRow row = _buffer.NewRow();
        row["FileName"] = Truncate(r.FileName, FileNameMaxLength);
        row["FullPath"] = r.FullPath;
        row["SizeBytes"] = r.SizeBytes;
        row["DateModifiedUtc"] = r.DateModifiedUtc;
        row["DateCreatedUtc"] = r.DateCreatedUtc;
        row["ContentHash"] = (object?)r.ContentHash ?? DBNull.Value;
        row["ScanError"] = (object?)r.Error ?? DBNull.Value;
        row["ScanRunId"] = _scanRunId;
        row["ScannedAtUtc"] = DateTime.UtcNow;
        _buffer.Rows.Add(row);

        if (_buffer.Rows.Count >= _options.BatchSize)
            await FlushAsync(ct);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Rows.Count == 0)
            return;

        using var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = _options.TableName,
            BulkCopyTimeout = 0,
            BatchSize = _options.BatchSize,
        };
        foreach (DataColumn col in _buffer.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(_buffer, ct);
        RowsWritten += _buffer.Rows.Count;
        _rowsWrittenThisScan += _buffer.Rows.Count;
        _buffer.Clear();
    }

    // --- helpers ---------------------------------------------------------

    private static DataTable BuildSchemaTable()
    {
        var t = new DataTable();
        t.Columns.Add("FileName", typeof(string));
        t.Columns.Add("FullPath", typeof(string));
        t.Columns.Add("SizeBytes", typeof(long));
        t.Columns.Add("DateModifiedUtc", typeof(DateTime));
        t.Columns.Add("DateCreatedUtc", typeof(DateTime));
        t.Columns.Add("ContentHash", typeof(string));
        t.Columns.Add("ScanError", typeof(string));
        t.Columns.Add("ScanRunId", typeof(string));
        t.Columns.Add("ScannedAtUtc", typeof(DateTime));
        return t;
    }

    private string DropTableSql() =>
        $"IF OBJECT_ID('{_options.TableName}', 'U') IS NOT NULL DROP TABLE {_options.TableName};";

    private string CreateTableSql() => $@"
IF OBJECT_ID('{_options.TableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.TableName} (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        FileName        NVARCHAR({FileNameMaxLength})  NOT NULL,
        FullPath        NVARCHAR(MAX)  NOT NULL,
        SizeBytes       BIGINT         NOT NULL,
        DateModifiedUtc DATETIME2(3)   NOT NULL,
        DateCreatedUtc  DATETIME2(3)   NOT NULL,
        ContentHash     CHAR(64)       NULL,
        ScanError       NVARCHAR(MAX)  NULL,
        ScanRunId       CHAR(32)       NOT NULL,
        ScannedAtUtc    DATETIME2(3)   NOT NULL
    );
    CREATE INDEX IX_Files_ContentHash ON {_options.TableName} (ContentHash) WHERE ContentHash IS NOT NULL;
    CREATE INDEX IX_Files_SizeBytes   ON {_options.TableName} (SizeBytes);
END";

    private string DropSkipsTableSql() =>
        $"IF OBJECT_ID('{_options.SkipTableName}', 'U') IS NOT NULL DROP TABLE {_options.SkipTableName};";

    private string CreateSkipsTableSql() => $@"
IF OBJECT_ID('{_options.SkipTableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.SkipTableName} (
        Id           BIGINT IDENTITY(1,1) PRIMARY KEY,
        FullPath     NVARCHAR(MAX)  NOT NULL,
        Reason       NVARCHAR(MAX)  NOT NULL,
        ScanRunId    CHAR(32)       NOT NULL,
        ScannedAtUtc DATETIME2(3)   NOT NULL
    );
    CREATE INDEX IX_ScanSkips_ScanRunId ON {_options.SkipTableName} (ScanRunId);
END";

    private string CreateScansTableSql() => $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.ScanTableName} (
        ScanRunId      CHAR(32)      NOT NULL PRIMARY KEY,
        StartedAtUtc   DATETIME2(3)  NOT NULL,
        CompletedAtUtc DATETIME2(3)  NULL,
        Status         NVARCHAR(20)  NOT NULL,
        MachineName    NVARCHAR(256) NULL,
        Drive          NVARCHAR(260) NULL,
        Roots          NVARCHAR(MAX) NULL,
        ComputeHash    BIT           NOT NULL,
        FilesWritten   BIGINT        NULL,
        ScanError      NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_Scans_Status ON {_options.ScanTableName} (Status);
    -- Speeds up ""latest completed scan per drive"" lookups during analysis.
    CREATE INDEX IX_Scans_Drive ON {_options.ScanTableName} (Drive, Status, CompletedAtUtc);
END";

    /// <summary>
    /// Add the per-drive <c>Drive</c> column to a scan log created before per-drive scans existed.
    /// Pre-existing rows keep a NULL <c>Drive</c> and are simply not matched by per-drive analysis.
    /// </summary>
    private string MigrateScansTableSql() => $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NOT NULL
   AND COL_LENGTH('{_options.ScanTableName}', 'Drive') IS NULL
BEGIN
    ALTER TABLE {_options.ScanTableName} ADD Drive NVARCHAR(260) NULL;
END";

    /// <summary>Create the target database if the connection points at one that doesn't exist yet.</summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(_options.ConnectionString);
        string dbName = builder.InitialCatalog;
        if (string.IsNullOrEmpty(dbName))
            return; // nothing named to create

        builder.InitialCatalog = "master";
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);
        await using var cmd = master.CreateCommand();
        cmd.CommandText = CreateDatabaseIfMissingSql;
        cmd.Parameters.AddWithValue("@db", dbName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _connection.Dispose();
}
