namespace DupeHunter;

/// <summary>Which kind of dupehunter YAML report a file holds.</summary>
public enum YamlReportKind
{
    /// <summary>No recognizable dupehunter report key was found.</summary>
    Unknown,

    /// <summary>A duplicates report (<see cref="DuplicateYamlReader"/>).</summary>
    Duplicates,

    /// <summary>A backup redundancy report (<see cref="BackupYamlReader"/>).</summary>
    Backup,
}

/// <summary>
/// Sniffs which report a YAML file is so a viewer can pick the right reader. Both reports share their
/// leading keys (<c>generatedUtc</c>, <c>scans</c>), so the probe scans the top-level keys until it
/// hits one unique to either format. Cheap and shallow — it never parses values, so a corrupt file
/// still gets classified and the real reader reports the precise error.
/// </summary>
public static class YamlReportProbe
{
    private static readonly string[] BackupKeys = ["backupRoot", "deletableFolders", "deletableFiles", "totalDeletableBytes"];
    private static readonly string[] DuplicateKeys = ["wastedSpaceThresholdBytes", "duplicateFileSets", "duplicateFolderSets", "totalWastedBytes"];

    public static async Task<YamlReportKind> DetectAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            // Only unindented "key:" lines are top-level keys; skip comments, blanks and nested content.
            if (line.Length == 0 || line[0] is ' ' or '#' or '-')
            {
                continue;
            }

            if (StartsWithAny(line, BackupKeys))
            {
                return YamlReportKind.Backup;
            }
            if (StartsWithAny(line, DuplicateKeys))
            {
                return YamlReportKind.Duplicates;
            }
        }
        return YamlReportKind.Unknown;
    }

    private static bool StartsWithAny(string line, string[] keys) =>
        keys.Any(key => line.StartsWith(key + ":", StringComparison.Ordinal));
}
