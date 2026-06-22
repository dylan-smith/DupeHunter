# harddrive-deduper

A Windows C# CLI that scans every file on every (or selected) hard drive and records each
file's metadata plus a content fingerprint into a SQL Server database. The content hash makes it
trivial to find duplicate files: any rows sharing a `ContentHash` are byte-for-byte identical.

## What it records

For every file it writes one row containing:

| Column            | Meaning                                              |
|-------------------|------------------------------------------------------|
| `FileName`        | File name only                                       |
| `FullPath`        | Absolute path                                        |
| `SizeBytes`       | Size in bytes                                         |
| `DateModifiedUtc` | Last write time (UTC)                                |
| `DateCreatedUtc`  | Creation time (UTC)                                  |
| `ContentHash`     | SHA-256 of the file contents (lower-case hex)        |
| `ScanError`       | Populated if the file couldn't be hashed (e.g. lock) |
| `ScanRunId`       | GUID identifying this scan run                       |
| `ScannedAtUtc`    | When the row was written                             |

The table is created automatically if it doesn't exist, as is the database named in the
connection string.

## Requirements

- .NET 10 SDK
- A reachable SQL Server instance

## Build

```
dotnet build -c Release
```

The executable is `fileindexer` (e.g. `bin/Release/net10.0/fileindexer.exe`).

## Usage

```
fileindexer [options]
```

| Option | Description |
|--------|-------------|
| `-d, --drives <list>` | Comma-separated drives to scan (`C,D` or `C:\,E:\`). Omit to scan **all fixed drives**. |
| `-c, --connection-string <s>` | SQL Server connection string. Default: `localhost` / `FileInventory` / integrated auth. |
| `-t, --table <name>` | Destination table. Default `dbo.Files`. |
| `--no-hash` | Record metadata only; skip hashing (much faster). |
| `--max-hash-mb <n>` | Skip hashing files larger than `n` MB (metadata still recorded). |
| `--batch-size <n>` | Rows per bulk-copy flush. Default 5000. |
| `--parallelism <n>` | Hashing threads. Default = processor count. |
| `--recreate` | Drop and recreate the table before scanning. |
| `--follow-links` | Follow directory symlinks/junctions (off by default to avoid loops). |
| `-h, --help` | Show help. |

### Examples

Scan all fixed drives into the default local database:

```
fileindexer
```

Scan only C: and D::

```
fileindexer --drives C,D
```

Use a specific server and credentials:

```
fileindexer -c "Server=.;Database=FileInventory;User Id=sa;Password=***;TrustServerCertificate=true"
```

Fast metadata-only inventory of C:, starting fresh:

```
fileindexer --drives C --no-hash --recreate
```

### Finding duplicates afterward

```sql
SELECT ContentHash, COUNT(*) AS Copies, SUM(SizeBytes) AS TotalBytes
FROM dbo.Files
WHERE ContentHash IS NOT NULL
GROUP BY ContentHash
HAVING COUNT(*) > 1
ORDER BY SUM(SizeBytes) DESC;
```

## Notes

- Hashing streams the file (1 MB buffer) so memory stays flat even on very large files.
- Files open in other processes are read with shared access where possible; unreadable files are
  still recorded with their metadata and a populated `ScanError`.
- Inaccessible directories (permissions) are skipped and counted, not fatal.
- Reparse points (symlinks/junctions) are skipped by default to avoid cycles and scanning network
  targets; use `--follow-links` to include them.
- Long paths (>260 chars) are supported via the application manifest on Windows 10 1607+ (the
  `LongPathsEnabled` system setting must also be on).
- Press **Ctrl-C** to stop; buffered rows are flushed before exit.
- Run elevated (Administrator) to maximize the set of readable system files.
```
