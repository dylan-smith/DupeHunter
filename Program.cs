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

// Ctrl-C => graceful flush & exit.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCancellation requested — flushing buffered rows...");
    cts.Cancel();
};

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
return exitCode;
