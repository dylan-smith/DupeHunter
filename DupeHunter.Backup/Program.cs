using DupeHunter;
using DupeHunter.Backup;

// Parse the command line first; bail out early on bad input or a help request.
BackupOptions options;
try
{
    options = BackupOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(BackupOptions.HelpText());
    return 0;
}

if (options.BackupRoot is null)
{
    Console.Error.WriteLine("Error: no backup folder was given. Usage: dupehunter-backup <backup-folder> [options]. Use --help for details.");
    return 2;
}

// Ctrl-C => graceful cancellation.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCancellation requested...");
    cts.Cancel();
};

BackupConsoleReporter.PrintBanner(options);

try
{
    var analyzer = new BackupAnalyzer(options);
    BackupReport report;
    BackupVerification verified;
    await using (var status = new StepProgress("Checking the backup against the scan database…"))
    {
        report = await analyzer.AnalyzeAsync(cts.Token, status);

        // The database's verdicts are only as fresh as the scans behind them: double-check every
        // deletable claim against the disk before the report is shown or written.
        verified = BackupReportVerifier.PruneStale(report, status, cts.Token);
    }

    if (verified.EntriesDropped > 0 || verified.LocationsDropped > 0)
    {
        Console.WriteLine(
            $"Disk check: dropped {verified.EntriesDropped} deletable item(s) and " +
            $"{verified.LocationsDropped} external location(s) missing or changed since the scan.");
    }

    BackupConsoleReporter.PrintReport(report, options.TopN);
    await WriteYamlReportAsync(report, cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Backup check canceled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Backup check failed:");
    Console.Error.WriteLine("  " + ex.Message);
    return 3;
}

// Write the YAML report unless --no-yaml was given. The default name carries the report's timestamp so
// successive runs each write a fresh file instead of clobbering. Non-fatal: a write failure warns and
// carries on (the console summary already ran).
async Task WriteYamlReportAsync(BackupReport report, CancellationToken ct)
{
    if (!options.WriteYaml)
    {
        return;
    }

    try
    {
        var outputPath = options.YamlOutputPath
            ?? $"backup-report-{report.GeneratedUtc:yyyyMMdd-HHmmss}.yml";
        await BackupYamlWriter.WriteAsync(outputPath, report, options.IncludeUnmatched, ct);
        Console.WriteLine();
        Console.WriteLine($"Report: {Path.GetFullPath(outputPath)}");
    }
    catch (OperationCanceledException) { /* user canceled; nothing partial to report */ }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Backup check succeeded, but writing the YAML report failed:");
        Console.Error.WriteLine("  " + ex.Message);
    }
}
