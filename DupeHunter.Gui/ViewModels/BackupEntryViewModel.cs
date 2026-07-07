using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

public enum BackupEntryState
{
    /// <summary>Still in the backup (as far as we know) and still listed in the report.</summary>
    Present,

    /// <summary>Permanently deleted from the backup this session.</summary>
    Deleted,

    /// <summary>Deliberately kept in the backup; its report entry was dismissed.</summary>
    Kept,

    /// <summary>Was already missing from disk; its stale report entry was removed.</summary>
    Removed,

    /// <summary>The last delete attempt failed; see <see cref="ErrorMessage"/>.</summary>
    Failed,
}

/// <summary>
/// One deletable entry of a backup report (a whole folder or a single file whose content exists
/// outside the backup tree), with its review actions: delete it from the backup, or keep it and
/// dismiss the suggestion. Either resolution removes the entry from the report, which is saved
/// immediately — the row stays visible, greyed out, as this session's record of what happened.
/// </summary>
public sealed partial class BackupEntryViewModel : ObservableObject
{
    private readonly BackupReportEntry _entry;
    private readonly BackupReportSession _session;
    private readonly IDialogService _dialogs;
    private readonly Action _onMutated;

    public BackupEntryViewModel(
        BackupReportEntry entry, bool isFolder, BackupReportSession session, IDialogService dialogs, Action onMutated)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
        IsFolder = isFolder;
        _session = session;
        _dialogs = dialogs;
        _onMutated = onMutated;
        Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.Path));
    }

    public bool IsFolder { get; }

    public string Name { get; }

    public string FullPath => _entry.Path;

    public long SizeBytes => _entry.SizeBytes;

    /// <summary>Descendant file count, formatted — folders only (blank for files).</summary>
    public string FileCountText => IsFolder ? _entry.FileCount.ToString("n0") : "";

    /// <summary>How the entry was matched to external content: exact, superset, or file.</summary>
    public string MatchKindText => _entry.MatchKind switch
    {
        BackupMatchKind.Exact => "exact",
        BackupMatchKind.Superset => "superset",
        BackupMatchKind.File => "file",
        _ => "file",
    };

    /// <summary>External locations holding this entry's content (a sample, not exhaustive).</summary>
    public IReadOnlyList<string> ExternalLocations => _entry.ExternalLocations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGone), nameof(StatusText), nameof(IsError))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(KeepCommand))]
    private BackupEntryState state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsError))]
    private string? errorMessage;

    /// <summary>True once the entry is resolved (deleted, kept, or found missing) and off the report.</summary>
    public bool IsGone => State is BackupEntryState.Deleted or BackupEntryState.Kept or BackupEntryState.Removed;

    public bool IsError => State == BackupEntryState.Failed;

    public string StatusText => State switch
    {
        BackupEntryState.Present => "",
        BackupEntryState.Deleted => "deleted from backup",
        BackupEntryState.Kept => "kept — removed from report",
        BackupEntryState.Removed => "was missing — removed from report",
        BackupEntryState.Failed => ErrorMessage ?? "failed",
        _ => "",
    };

    private bool CanAct() => !IsGone;

    /// <summary>The Delete button: guard, confirm, then delete the entry from the backup.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteAsync()
    {
        var exists = await Task.Run(() => IsFolder ? Directory.Exists(FullPath) : File.Exists(FullPath));
        if (!exists)
        {
            if (_dialogs.Confirm("Not found on disk",
                $"This entry no longer exists on disk:\n\n{FullPath}\n\nRemove its stale entry from the report?"))
            {
                RemoveFromReport();
                MarkGone(BackupEntryState.Removed);
                await SaveReportAsync();
            }
            return;
        }

        if (!_dialogs.ConfirmDanger("Delete from backup", BuildDeleteConfirmation()))
        {
            return;
        }

        var outcome = await _session.DeleteService.DeleteAsync(FullPath, IsFolder, CancellationToken.None);
        switch (outcome.Status)
        {
            case DeleteStatus.Deleted:
                RemoveFromReport();
                MarkGone(BackupEntryState.Deleted);
                await SaveReportAsync();
                break;

            case DeleteStatus.AlreadyMissing:
                // Vanished between listing and deleting; the report entry is stale either way.
                RemoveFromReport();
                MarkGone(BackupEntryState.Removed);
                await SaveReportAsync();
                break;

            case DeleteStatus.Locked:
                ErrorMessage = "in use by another process — close it and retry";
                State = BackupEntryState.Failed;
                break;

            case DeleteStatus.Failed:
            default:
                ErrorMessage = outcome.Error ?? "delete failed";
                State = BackupEntryState.Failed;
                break;
        }
    }

    /// <summary>
    /// The Keep button: dismiss the suggestion without touching the disk — the entry stays in the
    /// backup and comes off the report so it's never suggested again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task KeepAsync()
    {
        if (!_dialogs.Confirm("Keep in backup",
            $"Keep this {(IsFolder ? "folder" : "file")} in the backup?\n\n{FullPath}\n\nNothing is deleted from disk — the entry is only removed from the report, so it won't be suggested for deletion again."))
        {
            return;
        }

        RemoveFromReport();
        MarkGone(BackupEntryState.Kept);
        await SaveReportAsync();
    }

    [RelayCommand]
    private void OpenInExplorer() => ExplorerService.Reveal(FullPath);

    /// <summary>Reveal one of the external locations that justify this entry's deletion.</summary>
    [RelayCommand]
    private void RevealLocation(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            ExplorerService.Reveal(path);
        }
    }

    private string BuildDeleteConfirmation()
    {
        var message = new StringBuilder();
        var kind = IsFolder
            ? $"folder and ALL of its contents ({FileCountText} files)"
            : "file";
        message.AppendLine($"Permanently delete this {kind} from the backup?");
        message.AppendLine();
        message.AppendLine(FullPath);
        message.AppendLine();
        message.AppendLine($"Size: {Format.Bytes(SizeBytes)}");

        if (ExternalLocations.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("Its content also exists at:");
            const int maxListed = 3;
            foreach (var location in ExternalLocations.Take(maxListed))
            {
                message.AppendLine(location);
            }
            if (ExternalLocations.Count > maxListed)
            {
                message.AppendLine($"… and {ExternalLocations.Count - maxListed} more");
            }
        }

        message.AppendLine();
        message.Append("This does NOT use the Recycle Bin and cannot be undone.");
        return message.ToString();
    }

    private void RemoveFromReport()
    {
        if (IsFolder)
        {
            _session.Report.RemoveDeletableFolder(_entry);
        }
        else
        {
            _session.Report.RemoveDeletableFile(_entry);
        }
    }

    private void MarkGone(BackupEntryState newState)
    {
        ErrorMessage = null;
        State = newState;
        _onMutated();
    }

    /// <summary>Persist the edited report after a resolution.</summary>
    private async Task SaveReportAsync()
    {
        try
        {
            await _session.SaveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't update the report file",
                $"The change succeeded, but rewriting the report failed:\n{ex.Message}\n\nThe change is kept in this window and the next successful save will include it.");
        }
    }
}
