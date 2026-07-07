using System.Collections.ObjectModel;

namespace DupeHunter;

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
/// Like <see cref="DuplicateReport"/>, this doubles as the GUI's working model: a reviewing tool
/// removes entries as they are deleted from the backup (or kept on purpose) and persists the result
/// back through <see cref="BackupYamlWriter"/>.
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

    /// <summary>
    /// How many files must stay in the backup (content not found elsewhere). Stored rather than derived
    /// from <see cref="UnmatchedFiles"/>: the YAML only lists the unmatched files on request
    /// (--include-unmatched), and the count has to survive a round trip without the list.
    /// </summary>
    public int UnmatchedFileCount { get; set; }

    /// <summary>Bytes that must stay in the backup; stored for the same reason as <see cref="UnmatchedFileCount"/>.</summary>
    public long UnmatchedBytes { get; set; }

    /// <summary>Bytes that cannot be vouched for (never hashed).</summary>
    public long UnverifiableBytes => UnverifiableFiles.Sum(e => e.SizeBytes);

    /// <summary>
    /// Record that a deletable folder is resolved — deleted from the backup, or deliberately kept —
    /// either way it needs no further review. Returns false when the entry was already gone. Idempotent.
    /// </summary>
    public bool RemoveDeletableFolder(BackupReportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DeletableFolders.Remove(entry);
    }

    /// <inheritdoc cref="RemoveDeletableFolder"/>
    public bool RemoveDeletableFile(BackupReportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DeletableFiles.Remove(entry);
    }

    /// <summary>
    /// Drop every deletable entry that no longer exists on disk — deleted outside this report (by
    /// hand, by another tool, by an earlier session whose save was lost). Existence checks are
    /// injected so the model stays free of IO. Returns the number of entries removed.
    /// </summary>
    public int PruneMissingEntries(Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(folderExists);

        return RemoveEntriesWhere(DeletableFolders, path => !folderExists(path))
            + RemoveEntriesWhere(DeletableFiles, path => !fileExists(path));
    }

    private static int RemoveEntriesWhere(Collection<BackupReportEntry> entries, Func<string, bool> shouldRemove)
    {
        var removed = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (shouldRemove(entries[i].Path))
            {
                entries.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }
}
