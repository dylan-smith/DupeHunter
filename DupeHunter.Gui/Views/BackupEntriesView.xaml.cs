using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DupeHunter.Gui.Views;

/// <summary>
/// Reusable master/detail for one backup deletable-entry list: a virtualized grid of entries over a
/// pane of the selected entry's actions and the external locations that justify deleting it.
/// </summary>
public partial class BackupEntriesView : UserControl
{
    public static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(
        nameof(Entries), typeof(ICollectionView), typeof(BackupEntriesView));

    public BackupEntriesView() => InitializeComponent();

    public ICollectionView? Entries
    {
        get => (ICollectionView?)GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }
}
