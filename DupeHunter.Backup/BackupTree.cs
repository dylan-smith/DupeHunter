namespace DupeHunter.Backup;

/// <summary>A file's identity for content matching: hash plus size (the same pair the scanner's duplicate index uses).</summary>
internal readonly record struct ContentKey(string Hash, long SizeBytes);

/// <summary>One row read from the file inventory under the backup root.</summary>
internal readonly record struct BackupRow(
    bool IsFolder, string FullPath, long SizeBytes, string? ContentHash, string? ScanError);

/// <summary>A file inside the backup tree.</summary>
internal sealed class BackupFileNode
{
    public required string FullPath { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Content hash, or null when the scan never hashed the file (too big / read error).</summary>
    public string? Hash { get; init; }

    public string? ScanError { get; init; }
}

/// <summary>A folder inside the backup tree, with its scanned fingerprint and aggregates.</summary>
internal sealed class BackupFolderNode
{
    public required string FullPath { get; init; }

    /// <summary>
    /// The folder's content fingerprint from its 'D' row, or null when the folder is tainted (an
    /// unhashed descendant), the scan skipped folder fingerprints, or the node was synthesized.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>Sum of descendant file sizes (from the 'D' row, or computed for synthesized nodes).</summary>
    public long SizeBytes { get; set; }

    public List<BackupFolderNode> Folders { get; } = [];

    public List<BackupFileNode> Files { get; } = [];

    /// <summary>True when any descendant file has no hash — the folder can never be matched as a whole.</summary>
    public bool Tainted { get; set; }

    /// <summary>Number of descendant files (all levels).</summary>
    public long FileCount { get; set; }
}

/// <summary>Builds the in-memory backup tree from the inventory rows under the backup root.</summary>
internal static class BackupTreeBuilder
{
    /// <summary>
    /// Assemble the rows into a tree rooted at <paramref name="backupRoot"/>. Folder nodes come from 'D'
    /// rows when present; missing parents (scan ran with <c>--no-folder-hash</c>, or the root itself has
    /// no direct files) are synthesized with a null fingerprint, so exact matching degrades gracefully
    /// while superset matching still works. A bottom-up pass then fills in taint, file counts, and the
    /// sizes of synthesized nodes.
    /// </summary>
    public static BackupFolderNode Build(string backupRoot, IEnumerable<BackupRow> rows)
    {
        var folders = new Dictionary<string, BackupFolderNode>(StringComparer.OrdinalIgnoreCase)
        {
            [backupRoot] = new BackupFolderNode { FullPath = backupRoot },
        };
        var files = new List<BackupFileNode>();

        foreach (var row in rows)
        {
            if (row.IsFolder)
            {
                if (folders.TryGetValue(row.FullPath, out var existing))
                {
                    // Was synthesized as someone's parent before its own row arrived; fill in the real data.
                    existing.Fingerprint = row.ContentHash;
                    existing.SizeBytes = row.SizeBytes;
                }
                else
                {
                    folders[row.FullPath] = new BackupFolderNode
                    {
                        FullPath = row.FullPath,
                        Fingerprint = row.ContentHash,
                        SizeBytes = row.SizeBytes,
                    };
                }
            }
            else
            {
                files.Add(new BackupFileNode
                {
                    FullPath = row.FullPath,
                    SizeBytes = row.SizeBytes,
                    Hash = row.ContentHash,
                    ScanError = row.ScanError,
                });
            }
        }

        // Link every folder to its parent, synthesizing missing ancestors up to the root.
        foreach (var node in folders.Values.ToList())
        {
            if (PathsEqual(node.FullPath, backupRoot))
            {
                continue;
            }

            GetOrAddParent(folders, backupRoot, node.FullPath).Folders.Add(node);
        }

        foreach (var file in files)
        {
            GetOrAddParent(folders, backupRoot, file.FullPath).Files.Add(file);
        }

        var root = folders[backupRoot];
        Aggregate(root);
        return root;
    }

    /// <summary>The parent node of <paramref name="path"/>, creating (and linking) synthesized ancestors as needed.</summary>
    private static BackupFolderNode GetOrAddParent(
        Dictionary<string, BackupFolderNode> folders, string backupRoot, string path)
    {
        var parentPath = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"'{path}' has no parent directory but is not the backup root.");

        if (folders.TryGetValue(parentPath, out var parent))
        {
            return parent;
        }

        parent = new BackupFolderNode { FullPath = parentPath };
        folders[parentPath] = parent;
        if (!PathsEqual(parentPath, backupRoot))
        {
            GetOrAddParent(folders, backupRoot, parentPath).Folders.Add(parent);
        }

        return parent;
    }

    /// <summary>Bottom-up pass: taint, descendant file count, and sizes for synthesized nodes.</summary>
    private static void Aggregate(BackupFolderNode node)
    {
        long size = 0;
        long count = 0;
        var tainted = false;

        foreach (var file in node.Files)
        {
            size += file.SizeBytes;
            count++;
            tainted |= file.Hash is null;
        }

        foreach (var child in node.Folders)
        {
            Aggregate(child);
            size += child.SizeBytes;
            count += child.FileCount;
            tainted |= child.Tainted;
        }

        // A real 'D' row already carries the authoritative aggregate size; synthesized nodes get the sum.
        if (node.SizeBytes == 0)
        {
            node.SizeBytes = size;
        }

        node.FileCount = count;
        node.Tainted = tainted;
    }

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
