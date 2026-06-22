using System.Data;
using Microsoft.Data.SqlClient;

namespace HarddriveDeduper;

/// <summary>
/// Ensures the destination table exists and streams <see cref="FileRecord"/> rows into it
/// efficiently via <see cref="SqlBulkCopy"/>, flushing in batches.
/// </summary>
public sealed class DatabaseWriter : IDisposable
{
    private readonly Options _options;
    private readonly SqlConnection _connection;
    private readonly DataTable _buffer;
    private readonly string _scanRunId;

    public long RowsWritten;

    public DatabaseWriter(Options options)
    {
        _options = options;
        _connection = new SqlConnection(options.ConnectionString);
        _scanRunId = Guid.NewGuid().ToString("N");
        _buffer = BuildSchemaTable();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureDatabaseExistsAsync(ct);
        await _connection.OpenAsync(ct);

        if (_options.Recreate)
            await ExecuteAsync($"IF OBJECT_ID('{_options.TableName}', 'U') IS NOT NULL DROP TABLE {_options.TableName};", ct);

        await ExecuteAsync(CreateTableSql(), ct);
    }

    /// <summary>Add a record to the buffer; flushes automatically once the batch size is reached.</summary>
    public async Task AddAsync(FileRecord r, CancellationToken ct)
    {
        DataRow row = _buffer.NewRow();
        row["FileName"] = Truncate(r.FileName, 260);
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

    private string CreateTableSql() => $@"
IF OBJECT_ID('{_options.TableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.TableName} (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        FileName        NVARCHAR(260)  NOT NULL,
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
        cmd.CommandText = @"
IF DB_ID(@db) IS NULL
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@db);
    EXEC sp_executesql @sql;
END";
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
