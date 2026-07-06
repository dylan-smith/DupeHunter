using System.Text;

namespace DupeHunter.Backup;

public sealed partial class BackupOptions
{
    public static string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("dupehunter-backup - find files/folders inside a backup folder that already exist elsewhere,");
        sb.AppendLine("so they can be deleted from the backup to reclaim space. Reads only the scan database");
        sb.AppendLine("produced by dupehunter (run scans first); no disk contents are read or verified.");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        sb.AppendLine("  dupehunter-backup <backup-folder> [options]");
        sb.AppendLine();
        sb.AppendLine("Starting at the backup folder's top level, each folder is checked against everything");
        sb.AppendLine("outside the backup tree (latest completed scan of each drive):");
        sb.AppendLine("  - EXACT match:    an external folder holds exactly the same file contents");
        sb.AppendLine("                    (dupehunter's folder fingerprint; names/structure ignored).");
        sb.AppendLine("  - SUPERSET match: an external folder contains every distinct file content of the");
        sb.AppendLine("                    backup folder, plus possibly more (one external copy suffices).");
        sb.AppendLine("A matched folder is reported deletable and not descended into; an unmatched folder is");
        sb.AppendLine("descended one level and each child is checked the same way, down to individual files");
        sb.AppendLine("(deletable when an identical file exists outside the backup tree).");
        sb.AppendLine();
        sb.AppendLine("Files that were never hashed (too big / read errors) are 'unverifiable': they are kept and");
        sb.AppendLine("block every folder above them from matching as a whole. Folders with no files at all are");
        sb.AppendLine("not recorded by scans and so are invisible here (they hold no reclaimable space anyway).");
        sb.AppendLine();
        sb.AppendLine("OPTIONS:");
        sb.AppendLine("  -b, --backup <path>          The backup folder (alternative to the positional argument).");
        sb.AppendLine("  -c, --db, --database <path>  SQLite database file written by dupehunter scans.");
        sb.AppendLine("                               Default: dupehunter.db (in the current directory).");
        sb.AppendLine("  -d, --drives <list>          Comma-separated drives providing the external side");
        sb.AppendLine("                               (e.g. C,D). Must include the backup folder's own drive.");
        sb.AppendLine("                               Omit to use every scanned drive.");
        sb.AppendLine("      --exclude <path>         Folder whose contents never count as external copies");
        sb.AppendLine("                               (e.g. another backup folder). Repeatable.");
        sb.AppendLine("  -t, --table <name>           File inventory table. Default: Files");
        sb.AppendLine("      --scan-table <name>      Scan-run audit table. Default: Scans");
        sb.AppendLine("      --top <n>                Deletable entries to list on the console. Default: 20");
        sb.AppendLine("      --max-locations <n>      External locations reported per entry. Default: 3");
        sb.AppendLine("      --max-copies-per-key <n> Memory cap: external paths remembered per distinct file");
        sb.AppendLine("                               content. Default: 1000");
        sb.AppendLine("  -h, --help                   Show this help.");
        sb.AppendLine();
        sb.AppendLine("YAML REPORT:");
        sb.AppendLine("      --yaml-out <path>        Output file. Default: backup-report-<UTC timestamp>.yml");
        sb.AppendLine("      --include-unmatched      Also list every unmatched (keep) file, not just a count.");
        sb.AppendLine("      --no-yaml                Skip writing the YAML report.");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("  dupehunter-backup D:\\OldBackup");
        sb.AppendLine("  dupehunter-backup D:\\OldBackup --db D:\\index\\dupehunter.db");
        sb.AppendLine("  dupehunter-backup D:\\OldBackup --exclude E:\\OtherBackup --top 50");
        return sb.ToString();
    }
}
