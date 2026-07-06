namespace DupeHunter.Backup;

/// <summary>
/// Where the backup's content exists <em>outside</em> the backup tree. Built from the scan database:
/// for every distinct file content (and folder fingerprint) found in the backup, the external paths
/// holding the same content. Callers must feed only genuinely external paths (the analyzer drops
/// anything under the backup root or an excluded folder before it gets here).
/// </summary>
/// <remarks>
/// Per content, the copy <em>count</em> is always exact but at most <c>maxCopiesPerKey</c> paths are
/// stored (a memory cap for pathological contents — e.g. the zero-byte file, which every drive holds
/// thousands of). An over-cap content answers every containment probe with true: that can only
/// mis-attribute a <em>location</em>, never invent missing content, because deletability always
/// requires <see cref="ExternalCount"/> &gt; 0, which is exact.
/// </remarks>
internal sealed class ExternalCopyIndex
{
    private sealed class Entry
    {
        public int Count;
        public bool Overflowed;
        public List<string> Paths { get; } = [];
    }

    // Sort and binary search must share this comparer: strings sharing a case-folded prefix are
    // contiguous under OrdinalIgnoreCase ordering, which is what makes the prefix probe correct.
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<ContentKey, Entry> _files = [];
    private readonly Dictionary<ContentKey, List<string>> _folders = [];
    private readonly int _maxCopiesPerKey;
    private bool _sorted;

    public ExternalCopyIndex(int maxCopiesPerKey) => _maxCopiesPerKey = maxCopiesPerKey;

    /// <summary>Record an external file holding <paramref name="key"/>'s content.</summary>
    public void AddFileCopy(ContentKey key, string path)
    {
        if (!_files.TryGetValue(key, out var entry))
        {
            _files[key] = entry = new Entry();
        }

        entry.Count++;
        if (entry.Paths.Count < _maxCopiesPerKey)
        {
            entry.Paths.Add(path);
        }
        else
        {
            entry.Overflowed = true;
        }
    }

    /// <summary>Record an external folder whose fingerprint equals <paramref name="fingerprint"/>.</summary>
    public void AddFolderMatch(ContentKey fingerprint, string path)
    {
        if (!_folders.TryGetValue(fingerprint, out var paths))
        {
            _folders[fingerprint] = paths = [];
        }

        paths.Add(path);
    }

    /// <summary>Sort every path list once, after loading, so containment probes can binary search.</summary>
    public void SortForSearch()
    {
        foreach (var entry in _files.Values)
        {
            entry.Paths.Sort(PathComparer);
        }

        foreach (var paths in _folders.Values)
        {
            paths.Sort(PathComparer);
        }

        _sorted = true;
    }

    /// <summary>Exact number of external copies of this content; 0 means it exists nowhere else.</summary>
    public int ExternalCount(ContentKey key) => _files.TryGetValue(key, out var entry) ? entry.Count : 0;

    /// <summary>
    /// True when some external copy of <paramref name="key"/> lives under <paramref name="folderPrefix"/>
    /// (which must end with a path separator). Over-cap contents always answer true — see the class remarks.
    /// </summary>
    public bool HasCopyUnder(ContentKey key, string folderPrefix)
    {
        if (!_sorted)
        {
            throw new InvalidOperationException($"{nameof(SortForSearch)} must be called before probing.");
        }

        if (!_files.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.Overflowed)
        {
            return true;
        }

        // All paths starting with the prefix are contiguous and sort at/after the prefix itself.
        var i = entry.Paths.BinarySearch(folderPrefix, PathComparer);
        if (i < 0)
        {
            i = ~i;
        }

        return i < entry.Paths.Count && entry.Paths[i].StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The stored external paths for a file content (sorted; capped for pathological contents).</summary>
    public IReadOnlyList<string> FileCopies(ContentKey key) =>
        _files.TryGetValue(key, out var entry) ? entry.Paths : [];

    /// <summary>External folders whose fingerprint matches exactly (empty when none).</summary>
    public IReadOnlyList<string> ExactFolderMatches(ContentKey fingerprint) =>
        _folders.TryGetValue(fingerprint, out var paths) ? paths : [];
}
