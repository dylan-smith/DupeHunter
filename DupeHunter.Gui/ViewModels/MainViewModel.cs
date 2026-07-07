using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

/// <summary>
/// The main window: which YAML report is open — a duplicates report or a backup redundancy report,
/// sniffed by <see cref="YamlReportProbe"/> — the review lists built from it, and the
/// refresh/search/filter plumbing around them. All state comes from the report file the CLI wrote —
/// the scan database is never opened.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private ReportSession? _session;
    private BackupReportSession? _backupSession;

    public MainViewModel(IDialogService dialogs, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _settings = settings;

        var saved = settings.Load();
        reportPath = saved.LastReportPath ?? "";
        minWastedMb = saved.MinWastedMb;

        FileGroupsView = CollectionViewSource.GetDefaultView(FileGroups);
        FileGroupsView.Filter = MatchesSearch;
        FolderGroupsView = CollectionViewSource.GetDefaultView(FolderGroups);
        FolderGroupsView.Filter = MatchesSearch;
        BackupFolderEntriesView = CollectionViewSource.GetDefaultView(BackupFolderEntries);
        BackupFolderEntriesView.Filter = MatchesSearch;
        BackupFileEntriesView = CollectionViewSource.GetDefaultView(BackupFileEntries);
        BackupFileEntriesView.Filter = MatchesSearch;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string reportPath;

    [ObservableProperty]
    private double minWastedMb;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isBusy;

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    private string statusText = "Open a dupehunter report (.yml) to begin — a duplicates report or a backup report.";

    [ObservableProperty]
    private string scanSummary = "";

    [ObservableProperty]
    private string totalWastedText = "";

    /// <summary>True when the open report is a backup redundancy report; switches which tab set shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDuplicateReport))]
    private bool isBackupReport;

    public bool IsDuplicateReport => !IsBackupReport;

    public ObservableCollection<DuplicateGroupViewModel> FileGroups { get; } = [];

    public ObservableCollection<DuplicateGroupViewModel> FolderGroups { get; } = [];

    public ICollectionView FileGroupsView { get; }

    public ICollectionView FolderGroupsView { get; }

    public ObservableCollection<BackupEntryViewModel> BackupFolderEntries { get; } = [];

    public ObservableCollection<BackupEntryViewModel> BackupFileEntries { get; } = [];

    public ICollectionView BackupFolderEntriesView { get; }

    public ICollectionView BackupFileEntriesView { get; }

    /// <summary>Read-only context tabs of a backup report: what must stay and what can't be vouched for.</summary>
    [ObservableProperty]
    private IReadOnlyList<UnverifiableEntry> unverifiableFiles = [];

    [ObservableProperty]
    private IReadOnlyList<BackupReportEntry> unmatchedFiles = [];

    partial void OnSearchTextChanged(string value)
    {
        FileGroupsView.Refresh();
        FolderGroupsView.Refresh();
        BackupFolderEntriesView.Refresh();
        BackupFileEntriesView.Refresh();
    }

    private bool MatchesSearch(object item) => string.IsNullOrWhiteSpace(SearchText) || item switch
    {
        DuplicateGroupViewModel g => g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
        BackupEntryViewModel e => e.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    [RelayCommand]
    private async Task OpenReportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open dupehunter report",
            Filter = "Dupehunter report (*.yml;*.yaml)|*.yml;*.yaml|All files (*.*)|*.*",
            FileName = ReportPath,
        };
        if (dialog.ShowDialog() == true)
        {
            ReportPath = dialog.FileName;
            await RefreshAsync();
        }
    }

    private bool CanRefresh() => !IsBusy && !string.IsNullOrWhiteSpace(ReportPath);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!File.Exists(ReportPath))
        {
            _dialogs.ShowError("Report not found",
                $"No file at:\n{ReportPath}\n\nRun the dupehunter CLI (duplicates-<timestamp>.yml) or dupehunter-backup CLI (backup-report-<timestamp>.yml) first, then open the report it writes.");
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Loading report…";
            var kind = await YamlReportProbe.DetectAsync(ReportPath, CancellationToken.None);
            switch (kind)
            {
                case YamlReportKind.Duplicates:
                    await LoadDuplicateReportAsync();
                    break;
                case YamlReportKind.Backup:
                    await LoadBackupReportAsync();
                    break;
                case YamlReportKind.Unknown:
                default:
                    _dialogs.ShowError("Unrecognized report",
                        $"This file is not a dupehunter duplicates or backup report:\n{ReportPath}");
                    StatusText = "Load failed.";
                    return;
            }

            _settings.Save(new AppSettings { LastReportPath = ReportPath, MinWastedMb = MinWastedMb });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't load the report", ex.Message);
            StatusText = "Load failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDuplicateReportAsync()
    {
        var session = await Task.Run(() => ReportSession.LoadAsync(ReportPath, CancellationToken.None));

        _session = session;
        _backupSession = null;
        IsBackupReport = false;
        BackupFolderEntries.Clear();
        BackupFileEntries.Clear();
        UnverifiableFiles = [];
        UnmatchedFiles = [];

        Populate(FileGroups, session.Report.FileSets, isFolder: false);
        Populate(FolderGroups, session.Report.FolderSets, isFolder: true);

        ScanSummary = session.Report.Scans.Count == 0
            ? "No scans recorded in this report."
            : "Scans: " + string.Join("   ", session.Report.Scans.Select(s => $"{s.Drive} {s.CompletedAtUtc.ToLocalTime():g}"));
        TotalWastedText = $"Total reclaimable: {Format.Bytes(session.Report.TotalWastedBytes)}";
        StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets over {MinWastedMb:0.#} MB wasted.";
        if (session.PrunedOnLoad > 0)
        {
            StatusText += $" Pruned {session.PrunedOnLoad} location(s) no longer on disk.";
        }
    }

    private async Task LoadBackupReportAsync()
    {
        var session = await Task.Run(() => BackupReportSession.LoadAsync(ReportPath, CancellationToken.None));

        _backupSession = session;
        _session = null;
        IsBackupReport = true;
        FileGroups.Clear();
        FolderGroups.Clear();

        PopulateBackup(BackupFolderEntries, session.Report.DeletableFolders, isFolder: true);
        PopulateBackup(BackupFileEntries, session.Report.DeletableFiles, isFolder: false);
        UnverifiableFiles = [.. session.Report.UnverifiableFiles];
        UnmatchedFiles = [.. session.Report.UnmatchedFiles];

        ScanSummary = $"Backup: {session.Report.BackupRoot}";
        if (session.Report.Scans.Count > 0)
        {
            ScanSummary += "   Scans: " + string.Join("   ", session.Report.Scans.Select(s => $"{s.Drive} {s.CompletedAtUtc.ToLocalTime():g}"));
        }
        TotalWastedText = $"Total deletable: {Format.Bytes(session.Report.TotalDeletableBytes)}";
        StatusText = $"{BackupFolderEntries.Count} deletable folders, {BackupFileEntries.Count} deletable files over {MinWastedMb:0.#} MB.";
        if (session.PrunedOnLoad > 0)
        {
            StatusText += $" Pruned {session.PrunedOnLoad} entr{(session.PrunedOnLoad == 1 ? "y" : "ies")} no longer on disk.";
        }
    }

    /// <summary>
    /// Fill a list from the report's sets, hiding those below the min-wasted view filter (the report
    /// itself keeps them — the filter only affects what's shown).
    /// </summary>
    private void Populate(ObservableCollection<DuplicateGroupViewModel> target, IEnumerable<DuplicateReportSet> sets, bool isFolder)
    {
        var minBytes = (long)(MinWastedMb * 1024 * 1024);
        target.Clear();
        foreach (var set in sets.Where(s => s.WastedBytes >= minBytes))
        {
            target.Add(new DuplicateGroupViewModel(set, isFolder, _session!, _dialogs, RemoveResolvedGroup, RefreshFileGroupsAsync));
        }
    }

    /// <summary>
    /// Fill a list from the backup report's deletable entries, hiding those below the min-size view
    /// filter (the report itself keeps them — the filter only affects what's shown).
    /// </summary>
    private void PopulateBackup(ObservableCollection<BackupEntryViewModel> target, IEnumerable<BackupReportEntry> entries, bool isFolder)
    {
        var minBytes = (long)(MinWastedMb * 1024 * 1024);
        target.Clear();
        foreach (var entry in entries.Where(e => e.SizeBytes >= minBytes))
        {
            target.Add(new BackupEntryViewModel(entry, isFolder, _backupSession!, _dialogs, OnBackupEntryResolved));
        }
    }

    /// <summary>A backup entry was deleted, kept, or found missing — re-derive the totals it left behind.</summary>
    private void OnBackupEntryResolved()
    {
        if (_backupSession is null)
        {
            return;
        }

        TotalWastedText = $"Total deletable: {Format.Bytes(_backupSession.Report.TotalDeletableBytes)}";
        StatusText = $"{_backupSession.Report.DeletableFolders.Count} deletable folders, {_backupSession.Report.DeletableFiles.Count} deletable files left to review.";
    }

    /// <summary>A set is down to one copy — it's no longer a duplicate, so drop it from its list.</summary>
    private void RemoveResolvedGroup(DuplicateGroupViewModel group)
    {
        FileGroups.Remove(group);
        FolderGroups.Remove(group);
        StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets.";
    }

    /// <summary>
    /// Rebuild just the file groups after a folder tree was deleted (its files were group members
    /// too, and the report cascade stripped them). Cheap — it re-reads the in-memory report, and
    /// folder-set state is kept.
    /// </summary>
    private Task RefreshFileGroupsAsync()
    {
        if (_session is null)
        {
            return Task.CompletedTask;
        }

        Populate(FileGroups, _session.Report.FileSets, isFolder: false);
        TotalWastedText = $"Total reclaimable: {Format.Bytes(_session.Report.TotalWastedBytes)}";
        StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets (file view refreshed after folder delete).";
        return Task.CompletedTask;
    }
}
