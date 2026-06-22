using System.Security.Cryptography;

namespace HarddriveDeduper;

/// <summary>
/// Walks a directory tree manually (so a single inaccessible folder doesn't abort the whole
/// enumeration) and produces a <see cref="FileRecord"/> per file, optionally hashing contents.
/// </summary>
public sealed class FileScanner
{
    private readonly Options _options;

    public long FilesSeen;
    public long DirectoriesSkipped;
    public long HashErrors;

    public FileScanner(Options options) => _options = options;

    /// <summary>Lazily enumerate file metadata under <paramref name="root"/>. Hashing happens later.</summary>
    public IEnumerable<FileRecord> EnumerateFiles(string root)
    {
        // Explicit stack instead of recursion: avoids deep call stacks on long path chains and
        // lets us swallow access errors per-directory.
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

            // Queue sub-directories first.
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Interlocked.Increment(ref DirectoriesSkipped);
                continue;
            }

            foreach (string sub in subDirs)
            {
                if (!_options.FollowReparsePoints && IsReparsePoint(sub))
                    continue;
                pending.Push(sub);
            }

            // Then files in this directory.
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Interlocked.Increment(ref DirectoriesSkipped);
                continue;
            }

            foreach (string path in files)
            {
                FileRecord? record = TryReadMetadata(path);
                if (record is not null)
                {
                    Interlocked.Increment(ref FilesSeen);
                    yield return record;
                }
            }
        }
    }

    private static FileRecord? TryReadMetadata(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // Skip reparse-point files (symlinks) — the target is recorded elsewhere if reachable.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;

            return new FileRecord
            {
                FileName = info.Name,
                FullPath = info.FullName,
                SizeBytes = info.Length,
                DateModifiedUtc = info.LastWriteTimeUtc,
                DateCreatedUtc = info.CreationTimeUtc,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Compute the SHA-256 of a file's contents, streaming so memory stays flat on huge files.</summary>
    public void ComputeHash(FileRecord record)
    {
        if (!_options.ComputeHash)
            return;

        if (_options.MaxHashBytes > 0 && record.SizeBytes > _options.MaxHashBytes)
            return; // intentionally left null — metadata still recorded

        try
        {
            using var stream = new FileStream(
                record.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, // tolerate files held open by other processes
                bufferSize: 1 << 20,
                FileOptions.SequentialScan);

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            record.ContentHash = Convert.ToHexStringLower(hash);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref HashErrors);
            record.Error = ex.GetType().Name + ": " + ex.Message;
        }
    }

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
