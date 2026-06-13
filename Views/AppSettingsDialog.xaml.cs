using System.Collections.Generic;
using System.Collections.ObjectModel;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Confirmation step for Export App Settings: the folder matching is heuristic,
// so nothing is copied until the user has seen (and could untick) every match.
// Folder sizes are measured in the background after the dialog opens (each row
// shows "Calculating..." until its size lands), so opening is never blocked by
// a large settings tree.
public sealed partial class AppSettingsDialog : ContentDialog
{
    // Bound by x:Bind in the XAML.
    public ObservableCollection<AppSettingsCandidate> Candidates { get; } = new();

    private bool _measureStarted;

    public AppSettingsDialog(IEnumerable<AppSettingsCandidate> candidates)
    {
        foreach (var c in candidates) Candidates.Add(c);
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_measureStarted) return;
        _measureStarted = true;
        // One sequential background walk (parallel walks would just thrash the
        // disk); each row's labels refresh on the UI thread as its size lands.
        // The dialog can close mid-scan; the leftover rows then simply never
        // flip from "Calculating...", which nothing observes.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var c in Candidates)
            {
                AppSettingsExport.MeasureCandidate(c);
                DispatcherQueue.TryEnqueue(c.NotifyMeasured);
            }
        });
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var c in Candidates) c.Include = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var c in Candidates) c.Include = false;
    }
}
