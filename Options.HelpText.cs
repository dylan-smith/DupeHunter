using System.Text;

namespace HarddriveDeduper;

public sealed partial class Options
{
    public static string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("fileindexer - scan drives and record every file (with a content hash) into SQL Server.");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        sb.AppendLine("  fileindexer [options]");
        sb.AppendLine();
        sb.AppendLine("OPTIONS:");
        sb.AppendLine("  -d, --drives <list>          Comma-separated drives (e.g. C,D or C:\\,E:\\).");
        sb.AppendLine("                               Scan mode: drives to scan; each drive is its own scan run.");
        sb.AppendLine("                               Omit to scan ALL fixed drives.");
        sb.AppendLine("                               Analyze mode: drives whose latest scans to combine.");
        sb.AppendLine("  -c, --connection-string <s>  SQL Server connection string.");
        sb.AppendLine("                               Default: localhost / FileInventory / integrated auth.");
        sb.AppendLine("  -t, --table <name>           Destination table. Default: dbo.Files");
        sb.AppendLine("      --skip-table <name>      Table for skipped (inaccessible) directories.");
        sb.AppendLine("                               Default: dbo.ScanSkips");
        sb.AppendLine("      --scan-table <name>      Audit log of scan runs (start/finish time, status).");
        sb.AppendLine("                               Default: dbo.Scans (never dropped by --recreate)");
        sb.AppendLine("      --no-hash                Record metadata only; skip content hashing (much faster).");
        sb.AppendLine("      --max-hash-mb <n>        Skip hashing files larger than n MB (still records metadata).");
        sb.AppendLine("      --batch-size <n>         Rows per bulk-copy flush. Default: 5000");
        sb.AppendLine("      --parallelism <n>        Hashing threads. Default: processor count.");
        sb.AppendLine("      --recreate               Drop and recreate the table before scanning.");
        sb.AppendLine("      --follow-links           Follow directory symlinks/junctions (off by default).");
        sb.AppendLine("  -h, --help                   Show this help.");
        sb.AppendLine();
        sb.AppendLine("ANALYSIS (runs automatically after a scan; reports duplicate files):");
        sb.AppendLine("      --analyze, --duplicates  Analyze an existing table WITHOUT scanning. Combines the");
        sb.AppendLine("                               latest completed scan of each drive (or just the drives");
        sb.AppendLine("                               named with --drives) and reports files whose content");
        sb.AppendLine("                               (hash + size) appears in multiple locations, ranked by");
        sb.AppendLine("                               wasted space.");
        sb.AppendLine("      --no-analyze             Scan only; skip the post-scan duplicate analysis.");
        sb.AppendLine("      --top <n>                Number of duplicate sets to list. Default: 10");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("  fileindexer --drives C,D");
        sb.AppendLine("  fileindexer -c \"Server=.;Database=FileInventory;User Id=sa;Password=***;TrustServerCertificate=true\"");
        sb.AppendLine("  fileindexer --drives C --no-hash --recreate");
        sb.AppendLine("  fileindexer --analyze");
        sb.AppendLine("  fileindexer --analyze --top 25");
        sb.AppendLine("  fileindexer --analyze --drives C,D   (combine only C and D's latest scans)");
        return sb.ToString();
    }
}
