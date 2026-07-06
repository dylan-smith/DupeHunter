using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DupeHunter.Backup;

/// <summary>Parsed command-line options for dupehunter-backup. Help text lives in <c>BackupOptions.HelpText.cs</c>.</summary>
public sealed partial class BackupOptions
{
    /// <summary>
    /// The backup folder to check, normalized to a full path with no trailing separator (except a bare
    /// drive root like <c>D:\</c>). Set from the first non-option argument or <c>-b</c>/<c>--backup</c>.
    /// </summary>
    public string? BackupRoot { get; set; }

    /// <summary>Path of the SQLite database file produced by dupehunter scans.</summary>
    public string DatabasePath { get; set; } = "dupehunter.db";

    public string TableName { get; set; } = "Files";

    /// <summary>The scan-run audit table; identifies the latest completed scan per drive.</summary>
    public string ScanTableName { get; set; } = "Scans";

    /// <summary>Drives whose latest scans provide the external side, e.g. "C:\". Empty = all scanned drives.</summary>
    public Collection<string> Drives { get; } = [];

    /// <summary>
    /// Folders whose contents never count as external copies (normalized like <see cref="BackupRoot"/>),
    /// e.g. other backup folders. The backup tree itself is always excluded.
    /// </summary>
    public Collection<string> Excludes { get; } = [];

    /// <summary>Write the YAML report. On by default; disable with <c>--no-yaml</c>.</summary>
    public bool WriteYaml { get; set; } = true;

    /// <summary>
    /// Path of the YAML report. When null (the default) the file is auto-named
    /// <c>backup-report-{yyyyMMdd-HHmmss}.yml</c> with the run's UTC timestamp.
    /// </summary>
    public string? YamlOutputPath { get; set; }

    /// <summary>Also list every unmatched (keep) file in the YAML report, not just their count.</summary>
    public bool IncludeUnmatched { get; set; }

    /// <summary>How many deletable entries to list on the console (ranked by size).</summary>
    public int TopN { get; set; } = 20;

    /// <summary>External locations reported per deletable entry.</summary>
    public int MaxLocations { get; set; } = 3;

    /// <summary>
    /// Memory cap: external paths remembered per distinct file content. Contents with more copies than
    /// this keep an exact count but only this many sample paths (a containment probe on such a content
    /// always succeeds — it can only mis-attribute a location, never claim missing content exists).
    /// </summary>
    public int MaxCopiesPerKey { get; set; } = 1_000;

    public bool ShowHelp { get; set; }

    [SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity",
        Justification = "A flat switch with one case per command-line flag is the clearest form for an argument parser; splitting it would obscure rather than clarify.")]
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Option tokens are matched against lowercase ASCII flag names; lowercasing (not uppercasing) is what makes the comparison correct here.")]
    public static BackupOptions Parse(string[] args)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        var o = new BackupOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next(string name) => i + 1 >= args.Length ? throw new ArgumentException($"Option '{name}' requires a value.") : args[++i];

            if (!arg.StartsWith('-'))
            {
                SetBackupRoot(o, arg);
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "-?":
                    o.ShowHelp = true;
                    break;

                case "-b":
                case "--backup":
                    SetBackupRoot(o, Next(arg));
                    break;

                case "-c":
                case "--db":
                case "--database":
                    o.DatabasePath = Next(arg);
                    break;

                case "-t":
                case "--table":
                    o.TableName = Next(arg);
                    break;

                case "--scan-table":
                    o.ScanTableName = Next(arg);
                    break;

                case "-d":
                case "--drive":
                case "--drives":
                    foreach (var d in Next(arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        o.Drives.Add(NormalizeDrive(d));
                    }

                    break;

                case "--exclude":
                    o.Excludes.Add(NormalizePath(Next(arg)));
                    break;

                case "--no-yaml":
                    o.WriteYaml = false;
                    break;

                case "--yaml-out":
                    o.YamlOutputPath = Next(arg);
                    break;

                case "--include-unmatched":
                    o.IncludeUnmatched = true;
                    break;

                case "--top":
                    o.TopN = Math.Max(1, int.Parse(Next(arg)));
                    break;

                case "--max-locations":
                    o.MaxLocations = Math.Max(1, int.Parse(Next(arg)));
                    break;

                case "--max-copies-per-key":
                    o.MaxCopiesPerKey = Math.Max(1, int.Parse(Next(arg)));
                    break;

                default:
                    throw new ArgumentException($"Unknown option: '{arg}'. Use --help for usage.");
            }
        }

        return o;
    }

    /// <summary>The Core options used to open the scan database (connection string + table names).</summary>
    public Options ToCoreOptions()
    {
        var core = new Options
        {
            DatabasePath = DatabasePath,
            TableName = TableName,
            ScanTableName = ScanTableName,
        };
        foreach (var d in Drives)
        {
            core.Drives.Add(d);
        }

        return core;
    }

    private static void SetBackupRoot(BackupOptions o, string raw)
    {
        if (o.BackupRoot is not null)
        {
            throw new ArgumentException($"Backup folder was given twice ('{o.BackupRoot}' and '{raw}').");
        }

        o.BackupRoot = NormalizePath(raw);
    }

    /// <summary>
    /// Canonicalize a path for prefix comparison: full path, no trailing separator — except a bare drive
    /// root (<c>D:\</c>), which keeps its backslash to match how scans record drive roots.
    /// </summary>
    internal static string NormalizePath(string raw)
    {
        var full = Path.GetFullPath(raw);
        var trimmed = full.TrimEnd('\\', '/');
        // "D:" would be a drive-relative path, not the root; keep the root form "D:\".
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    /// <summary>Turn "c" / "C:" / "C:\" into the canonical root form "C:\".</summary>
    private static string NormalizeDrive(string raw)
    {
        var s = raw.Trim().TrimEnd('\\', '/');
        if (s.Length == 1 && char.IsLetter(s[0]))
        {
            s += ":";
        }

        if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
        {
            return s.ToUpperInvariant() + "\\";
        }
        // Fall back to whatever the user gave (could be a UNC path or mount point).
        return raw.EndsWith('\\') ? raw : raw + "\\";
    }
}
