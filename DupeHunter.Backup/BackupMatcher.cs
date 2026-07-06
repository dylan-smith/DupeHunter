namespace DupeHunter.Backup;

/// <summary>
/// Walks the backup tree top-down applying the deletability rules. A folder is deletable when an
/// external folder is an exact content match (fingerprint equality) or contains every distinct file
/// content of the backup folder (superset). A matched folder is recorded and not descended into; an
/// unmatched folder is descended one level and each child checked the same way, down to individual
/// files. Because matched folders are never descended, the report's deletable folders and files never
/// overlap.
/// </summary>
internal sealed class BackupMatcher
{
    private readonly ExternalCopyIndex _index;
    private readonly int _maxLocations;
    private readonly BackupReport _report;

    public BackupMatcher(ExternalCopyIndex index, int maxLocations, BackupReport report)
    {
        _index = index;
        _maxLocations = maxLocations;
        _report = report;
    }

    /// <summary>Walk from the backup root itself, so a fully redundant backup yields one root entry.</summary>
    public void Run(BackupFolderNode root) => Walk(root);

    private void Walk(BackupFolderNode node)
    {
        if (TryMatchFolder(node, out var kind, out var locations))
        {
            var entry = new BackupReportEntry
            {
                Path = node.FullPath,
                SizeBytes = node.SizeBytes,
                MatchKind = kind,
                FileCount = node.FileCount,
            };
            foreach (var location in locations)
            {
                entry.ExternalLocations.Add(location);
            }

            _report.DeletableFolders.Add(entry);
            return;
        }

        foreach (var file in node.Files)
        {
            ClassifyFile(file);
        }

        foreach (var child in node.Folders)
        {
            Walk(child);
        }
    }

    private bool TryMatchFolder(BackupFolderNode node, out BackupMatchKind kind, out List<string> locations)
    {
        kind = default;
        locations = [];

        // An unhashed descendant means no external folder can ever vouch for all of this folder's
        // content — the fingerprint is null by the scanner's taint rule, and a superset check can't
        // cover content whose hash is unknown.
        if (node.Tainted)
        {
            return false;
        }

        // Every distinct content beneath the folder must exist somewhere external, or no single folder
        // can possibly contain them all — bail as soon as one content is missing.
        var keys = new HashSet<ContentKey>();
        if (!CollectKeys(node, keys) || keys.Count == 0)
        {
            return false;
        }

        // Exact: an external folder with the very same fingerprint (same content multiset, counts and all).
        if (node.Fingerprint is not null)
        {
            var exact = _index.ExactFolderMatches(new ContentKey(node.Fingerprint, node.SizeBytes));
            if (exact.Count > 0)
            {
                kind = BackupMatchKind.Exact;
                locations = exact.Take(_maxLocations).ToList();
                return true;
            }
        }

        // Superset: candidate folders are the ancestors of the external copies of the folder's rarest
        // content — any folder containing everything must in particular contain that one, so (unless the
        // rarest content is over the path cap, where the sample may be incomplete) the candidate list is
        // exhaustive. Deepest-first so the reported location is the most specific containing folder.
        var rarest = keys.MinBy(k => _index.ExternalCount(k));
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in _index.FileCopies(rarest))
        {
            for (var dir = Path.GetDirectoryName(copy); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                candidates.Add(dir);
            }
        }

        foreach (var candidate in candidates.OrderByDescending(Depth))
        {
            // An ancestor of a folder already found trivially matches too but adds no information;
            // keep only the deepest (most specific) folder of each containing chain.
            if (locations.Any(found => BackupAnalyzer.IsUnderOrEqual(found, candidate)))
            {
                continue;
            }

            var prefix = BackupAnalyzer.WithSeparator(candidate);
            if (keys.All(k => _index.HasCopyUnder(k, prefix)))
            {
                locations.Add(candidate);
                if (locations.Count >= _maxLocations)
                {
                    break;
                }
            }
        }

        if (locations.Count > 0)
        {
            kind = BackupMatchKind.Superset;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gather the distinct contents of every file beneath <paramref name="node"/>. False (fail fast) as
    /// soon as one content has no external copy — the folder can't be matched as a whole.
    /// </summary>
    private bool CollectKeys(BackupFolderNode node, HashSet<ContentKey> keys)
    {
        foreach (var file in node.Files)
        {
            if (file.Hash is null)
            {
                return false; // unreachable behind the taint gate, but never vouch for unhashed content
            }

            var key = new ContentKey(file.Hash, file.SizeBytes);
            if (keys.Add(key) && _index.ExternalCount(key) == 0)
            {
                return false;
            }
        }

        foreach (var child in node.Folders)
        {
            if (!CollectKeys(child, keys))
            {
                return false;
            }
        }

        return true;
    }

    private void ClassifyFile(BackupFileNode file)
    {
        if (file.Hash is null)
        {
            _report.UnverifiableFiles.Add(new UnverifiableEntry
            {
                Path = file.FullPath,
                SizeBytes = file.SizeBytes,
                Reason = file.ScanError ?? "not hashed",
            });
            return;
        }

        var key = new ContentKey(file.Hash, file.SizeBytes);
        var copies = _index.FileCopies(key);
        if (copies.Count == 0)
        {
            _report.UnmatchedFiles.Add(new BackupReportEntry
            {
                Path = file.FullPath,
                SizeBytes = file.SizeBytes,
                MatchKind = BackupMatchKind.File,
            });
            return;
        }

        var entry = new BackupReportEntry
        {
            Path = file.FullPath,
            SizeBytes = file.SizeBytes,
            MatchKind = BackupMatchKind.File,
        };
        foreach (var copy in copies.Take(_maxLocations))
        {
            entry.ExternalLocations.Add(copy);
        }

        _report.DeletableFiles.Add(entry);
    }

    private static int Depth(string path) => path.Count(c => c == '\\');
}
