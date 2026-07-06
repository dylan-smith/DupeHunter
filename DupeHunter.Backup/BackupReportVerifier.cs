using System.Collections.ObjectModel;

namespace DupeHunter.Backup;

/// <summary>What the disk check removed from a report's deletable lists.</summary>
public sealed record BackupVerification(int EntriesDropped, int LocationsDropped);

/// <summary>
/// Double-checks a <see cref="BackupReport"/>'s deletable entries against the disk before the report
/// is shown or written. The database's verdicts are only as fresh as the scans behind them, so every
/// claim that backup content is safe to delete is re-tested on both sides: the backup entry itself
/// must still exist with the scanned size (a file's length; a folder's recursive file-size total,
/// walked with the same skip rules as the scanner so an unchanged tree sums to the very number the
/// scan recorded), and so must the external copies vouching for it. Stale external locations are
/// dropped from their entry; an entry whose own path is stale, or whose every listed location failed
/// the check, leaves the deletable lists entirely — when in doubt, keep the backup.
/// </summary>
/// <remarks>
/// An entry's <see cref="BackupReportEntry.ExternalLocations"/> is a sample capped by
/// <see cref="BackupOptions.MaxLocations"/>, so the database may know further copies beyond the ones
/// checked here; an entry is still dropped when every <em>listed</em> location is stale, which errs
/// toward keeping data. Superset folder locations can only be checked for existence: such a folder
/// holds every distinct content of the backup folder (possibly collapsing copies the backup holds
/// several times, plus unrelated extras), so its size is unrelated to the entry's.
/// </remarks>
public static class BackupReportVerifier
{
    /// <summary>Prune every deletable entry and external location the disk no longer backs up.</summary>
    public static BackupVerification PruneStale(BackupReport report, IStepProgress? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var total = report.DeletableFolders.Count + report.DeletableFiles.Count;
        var seen = 0;
        var entriesDropped = 0;
        var locationsDropped = 0;
        progress?.BeginStep("Verifying deletable entries against the disk…");

        Verify(report.DeletableFolders, entryIsFolder: true);
        Verify(report.DeletableFiles, entryIsFolder: false);
        return new BackupVerification(entriesDropped, locationsDropped);

        void Verify(Collection<BackupReportEntry> entries, bool entryIsFolder)
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                progress?.UpdateStep($"Verifying deletable entries against the disk ({++seen}/{total})…");

                var entry = entries[i];
                var entryIntact = entryIsFolder
                    ? FolderIntact(entry.Path, entry.SizeBytes, ct)
                    : FileIntact(entry.Path, entry.SizeBytes);
                if (!entryIntact)
                {
                    // The backup entry itself vanished (nothing left to delete) or changed size (its
                    // content no longer matches what the scan vouched for).
                    entries.RemoveAt(i);
                    entriesDropped++;
                    continue;
                }

                for (var j = entry.ExternalLocations.Count - 1; j >= 0; j--)
                {
                    if (!LocationIntact(entry, entry.ExternalLocations[j], ct))
                    {
                        entry.ExternalLocations.RemoveAt(j);
                        locationsDropped++;
                    }
                }

                if (entry.ExternalLocations.Count == 0)
                {
                    // Every copy that justified deleting this entry is gone or changed.
                    entries.RemoveAt(i);
                    entriesDropped++;
                }
            }
        }
    }

    /// <summary>An external location still holds what the match kind claimed it holds.</summary>
    private static bool LocationIntact(BackupReportEntry entry, string location, CancellationToken ct) =>
        entry.MatchKind switch
        {
            BackupMatchKind.File => FileIntact(location, entry.SizeBytes),
            BackupMatchKind.Exact => FolderIntact(location, entry.SizeBytes, ct),
            BackupMatchKind.Superset => Directory.Exists(location),
            _ => false,
        };

    /// <summary>A file still exists at <paramref name="path"/> with exactly the scanned size.</summary>
    private static bool FileIntact(string path, long expectedBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == expectedBytes;
        }
        catch (Exception ex) when (IsSkippable(ex))
        {
            // Can't inspect it, so don't vouch for it.
            return false;
        }
    }

    /// <summary>
    /// A folder still exists at <paramref name="path"/> and its recursive file-size total still
    /// equals the scanned size. The walk mirrors the scanner: directories that can't be enumerated
    /// contribute nothing (the scan skipped them too, so their files were never in the recorded
    /// total), and reparse points are skipped. Bails out early once the running total exceeds the
    /// expected size.
    /// </summary>
    private static bool FolderIntact(string path, long expectedBytes, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        long sum = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!IsReparsePoint(sub))
                    {
                        pending.Push(sub);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (TryFileLength(file) is { } length)
                    {
                        sum += length;
                        if (sum > expectedBytes)
                        {
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                continue;
            }
        }

        return sum == expectedBytes;
    }

    /// <summary>
    /// The file's length, or null when it is a reparse point or can't be read — the same files the
    /// scanner left out of the folder totals.
    /// </summary>
    private static long? TryFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? null : info.Length;
        }
        catch (Exception ex) when (IsSkippable(ex))
        {
            return null;
        }
    }

    /// <summary>The filesystem errors the scanner tolerates per entry rather than aborting on.</summary>
    private static bool IsSkippable(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or System.Security.SecurityException;

    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }
}
