using System.Diagnostics;
using HarddriveDeduper;

// Parse the command line first; bail out early on bad input or a help request.
Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.HelpText());
    return 0;
}

// Ctrl-C => graceful cancellation for whichever mode we run.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCancellation requested...");
    cts.Cancel();
};

// Analysis mode reads an already-scanned table and exits; it never touches the drives.
if (options.Analyze)
{
    var analysisReporter = new ConsoleReporter(options);
    try
    {
        var analyzer = new DuplicateAnalyzer(options);
        DuplicateAnalysis analysis =
            await analyzer.FindTopDuplicatesAsync(options.TopN, samplePathsPerGroup: 5, cts.Token);
        analysisReporter.PrintDuplicates(analysis, options.TopN);
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Analysis canceled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Analysis failed:");
        Console.Error.WriteLine("  " + ex.Message);
        return 3;
    }
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Warning: this tool targets Windows drive semantics; behavior on other platforms is best-effort.");
}

List<string> roots = DriveResolver.ResolveRoots(options);
if (roots.Count == 0)
{
    Console.Error.WriteLine("No drives to scan.");
    return 1;
}

var reporter = new ConsoleReporter(options);
reporter.PrintBanner(roots);

var scanner = new FileScanner(options);
using var writer = new DatabaseWriter(options);

try
{
    await writer.InitializeAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to connect / initialize database:");
    Console.Error.WriteLine("  " + ex.Message);
    return 3;
}

var sw = Stopwatch.StartNew();
int exitCode = await new ScanPipeline(options, scanner, writer, reporter).RunAsync(roots, cts.Token);
sw.Stop();

reporter.PrintSummary(scanner, writer, sw.Elapsed);

// Once the scan completes cleanly, surface duplicates straight away (unless suppressed, or there
// are no hashes to compare). Use --no-analyze to skip, or --analyze on its own to re-run later.
if (exitCode == 0 && options.ComputeHash && !options.NoAnalyze)
{
    Console.WriteLine();
    try
    {
        var analyzer = new DuplicateAnalyzer(options);
        DuplicateAnalysis analysis =
            await analyzer.FindTopDuplicatesAsync(options.TopN, samplePathsPerGroup: 5, cts.Token);
        reporter.PrintDuplicates(analysis, options.TopN);
    }
    catch (OperationCanceledException) { /* user canceled; summary already printed */ }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Scan succeeded, but duplicate analysis failed:");
        Console.Error.WriteLine("  " + ex.Message);
    }
}

return exitCode;
