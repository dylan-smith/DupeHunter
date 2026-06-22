namespace HarddriveDeduper;

/// <summary>A single file's metadata plus a content fingerprint, destined for the database.</summary>
public sealed class FileRecord
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime DateModifiedUtc { get; init; }
    public required DateTime DateCreatedUtc { get; init; }

    /// <summary>SHA-256 of the file contents, lower-case hex. Null when hashing was skipped or failed.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Populated when the file could not be read/hashed; otherwise null.</summary>
    public string? Error { get; set; }
}
