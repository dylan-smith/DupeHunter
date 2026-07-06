using System.Collections.ObjectModel;

namespace DupeHunter.Backup;

/// <summary>How a deletable backup entry was matched to external content.</summary>
public enum BackupMatchKind
{
    /// <summary>An external folder holds exactly the same file contents (fingerprint equality).</summary>
    Exact,

    /// <summary>An external folder contains every distinct file content of the backup folder, plus more.</summary>
    Superset,

    /// <summary>An identical file (content hash + size) exists outside the backup tree.</summary>
    File,
}

/// <summary>One backup file or folder together with the external locations that justify its verdict.</summary>
public sealed class BackupReportEntry
{
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    public required BackupMatchKind MatchKind { get; init; }

    /// <summary>Descendant file count — folders only (0 for files).</summary>
    public long FileCount { get; init; }

    /// <summary>External locations containing the entry's content (a sample, not exhaustive). Empty for unmatched files.</summary>
    public Collection<string> ExternalLocations { get; } = [];
}

/// <summary>A backup file whose content was never hashed, so no external match can vouch for it.</summary>
public sealed class UnverifiableEntry
{
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Why the file has no hash (the scan's recorded error, or "not hashed").</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// The result of checking one backup folder against the scan database: which entries can be deleted
/// because their content exists outside the backup tree, and which must be kept. Deletable folders are
/// never descended into, so the deletable folder/file lists never overlap and their bytes sum cleanly.
/// </summary>
public sealed class BackupReport
{
    public required DateTime GeneratedUtc { get; init; }

    public required string BackupRoot { get; init; }

    /// <summary>Folders excluded from the external side (beyond the backup tree itself).</summary>
    public Collection<string> Excludes { get; } = [];

    /// <summary>The scan runs consulted (latest completed scan per drive).</summary>
    public Collection<ScanRef> Scans { get; } = [];

    /// <summary>Whole folders whose content exists externally; safe to delete without descending.</summary>
    public Collection<BackupReportEntry> DeletableFolders { get; } = [];

    /// <summary>Individual files (inside unmatched folders) whose content exists externally.</summary>
    public Collection<BackupReportEntry> DeletableFiles { get; } = [];

    /// <summary>Files whose content exists nowhere outside the backup tree — keep these.</summary>
    public Collection<BackupReportEntry> UnmatchedFiles { get; } = [];

    /// <summary>Files with no hash in the database; kept, and they block their ancestors from matching.</summary>
    public Collection<UnverifiableEntry> UnverifiableFiles { get; } = [];

    /// <summary>Space reclaimed by deleting every deletable folder and file.</summary>
    public long TotalDeletableBytes =>
        DeletableFolders.Sum(e => e.SizeBytes) + DeletableFiles.Sum(e => e.SizeBytes);

    /// <summary>Bytes that must stay in the backup (content not found elsewhere).</summary>
    public long UnmatchedBytes => UnmatchedFiles.Sum(e => e.SizeBytes);

    /// <summary>Bytes that cannot be vouched for (never hashed).</summary>
    public long UnverifiableBytes => UnverifiableFiles.Sum(e => e.SizeBytes);
}
