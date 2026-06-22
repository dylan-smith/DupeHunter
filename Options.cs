using System.Text;

namespace HarddriveDeduper;

/// <summary>Parsed command-line options.</summary>
public sealed class Options
{
    /// <summary>Drive roots to scan, e.g. "C:\", "D:\". Empty means "all fixed drives".</summary>
    public List<string> Drives { get; } = new();

    public string ConnectionString { get; set; } =
        "Server=localhost;Database=FileInventory;Integrated Security=true;TrustServerCertificate=true;";

    public string TableName { get; set; } = "dbo.Files";

    /// <summary>When true, file contents are hashed (SHA-256). Disable for a fast metadata-only inventory.</summary>
    public bool ComputeHash { get; set; } = true;

    /// <summary>Skip hashing files larger than this (bytes). 0 = no limit.</summary>
    public long MaxHashBytes { get; set; } = 0;

    /// <summary>Rows accumulated before each bulk-copy flush.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>Degree of parallelism for hashing. Defaults to processor count.</summary>
    public int Parallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Drop &amp; recreate the destination table before scanning.</summary>
    public bool Recreate { get; set; }

    /// <summary>Follow directory reparse points (symlinks / junctions). Off by default to avoid loops.</summary>
    public bool FollowReparsePoints { get; set; }

    public bool ShowHelp { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next(string name)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Option '{name}' requires a value.");
                return args[++i];
            }

            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "-?":
                    o.ShowHelp = true;
                    break;

                case "-d":
                case "--drive":
                case "--drives":
                    foreach (var d in Next(arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        o.Drives.Add(NormalizeDrive(d));
                    break;

                case "-c":
                case "--connection-string":
                    o.ConnectionString = Next(arg);
                    break;

                case "-t":
                case "--table":
                    o.TableName = Next(arg);
                    break;

                case "--no-hash":
                    o.ComputeHash = false;
                    break;

                case "--max-hash-mb":
                    o.MaxHashBytes = long.Parse(Next(arg)) * 1024L * 1024L;
                    break;

                case "--batch-size":
                    o.BatchSize = int.Parse(Next(arg));
                    break;

                case "--parallelism":
                    o.Parallelism = Math.Max(1, int.Parse(Next(arg)));
                    break;

                case "--recreate":
                    o.Recreate = true;
                    break;

                case "--follow-links":
                    o.FollowReparsePoints = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown option: '{arg}'. Use --help for usage.");
            }
        }

        return o;
    }

    /// <summary>Turn "c" / "C:" / "C:\" into the canonical root form "C:\".</summary>
    private static string NormalizeDrive(string raw)
    {
        string s = raw.Trim().TrimEnd('\\', '/');
        if (s.Length == 1 && char.IsLetter(s[0]))
            s += ":";
        if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
            return s.ToUpperInvariant() + "\\";
        // Fall back to whatever the user gave (could be a UNC path or mount point).
        return raw.EndsWith('\\') ? raw : raw + "\\";
    }

    public static string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("fileindexer - scan drives and record every file (with a content hash) into SQL Server.");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        sb.AppendLine("  fileindexer [options]");
        sb.AppendLine();
        sb.AppendLine("OPTIONS:");
        sb.AppendLine("  -d, --drives <list>          Comma-separated drives to scan (e.g. C,D or C:\\,E:\\).");
        sb.AppendLine("                               Omit to scan ALL fixed drives.");
        sb.AppendLine("  -c, --connection-string <s>  SQL Server connection string.");
        sb.AppendLine("                               Default: localhost / FileInventory / integrated auth.");
        sb.AppendLine("  -t, --table <name>           Destination table. Default: dbo.Files");
        sb.AppendLine("      --no-hash                Record metadata only; skip content hashing (much faster).");
        sb.AppendLine("      --max-hash-mb <n>        Skip hashing files larger than n MB (still records metadata).");
        sb.AppendLine("      --batch-size <n>         Rows per bulk-copy flush. Default: 5000");
        sb.AppendLine("      --parallelism <n>        Hashing threads. Default: processor count.");
        sb.AppendLine("      --recreate               Drop and recreate the table before scanning.");
        sb.AppendLine("      --follow-links           Follow directory symlinks/junctions (off by default).");
        sb.AppendLine("  -h, --help                   Show this help.");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("  fileindexer --drives C,D");
        sb.AppendLine("  fileindexer -c \"Server=.;Database=FileInventory;User Id=sa;Password=***;TrustServerCertificate=true\"");
        sb.AppendLine("  fileindexer --drives C --no-hash --recreate");
        return sb.ToString();
    }
}
