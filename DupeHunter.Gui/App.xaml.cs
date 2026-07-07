using System.IO;
using System.Windows;
using DupeHunter.Gui.Services;
using DupeHunter.Gui.ViewModels;
using DupeHunter.Gui.Views;

namespace DupeHunter.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var viewModel = new MainViewModel(new DialogService(), new SettingsService());

        // First run (no saved path): offer the newest CLI report if one is sitting next to us —
        // a duplicates report or a backup report, whichever was written last.
        if (string.IsNullOrWhiteSpace(viewModel.ReportPath))
        {
            var newest = Directory.EnumerateFiles(Environment.CurrentDirectory, "duplicates-*.yml")
                .Concat(Directory.EnumerateFiles(Environment.CurrentDirectory, "backup-report-*.yml"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                viewModel.ReportPath = Path.GetFullPath(newest);
            }
        }

        // The pre-filled path is only an offer — loading can take minutes (every location is
        // checked against disk), so nothing loads until the user asks with Refresh or Open.
        if (!string.IsNullOrWhiteSpace(viewModel.ReportPath))
        {
            viewModel.StatusText = "Press Refresh to load the report above, or Open… to pick another.";
        }

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
