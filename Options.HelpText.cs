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
