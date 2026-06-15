using System.Collections.Generic;
using System.Collections.ObjectModel;
using GuardWui3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Confirmation step for Reinstall & Restore Settings: the restore overwrites
// real per-user data, so nothing is touched until the user has seen each target
// path (and its "Replaces existing" warning) and could untick it. Sizes come
// straight from the export manifest, so unlike the export dialog there is no
// background measuring to wait on.
public sealed partial class AppSettingsRestoreDialog : ContentDialog
{
    // Bound by x:Bind in the XAML.
    public ObservableCollection<AppSettingsRestoreCandidate> Candidates { get; } = new();
    public string HeaderText { get; }

    public AppSettingsRestoreDialog(IEnumerable<AppSettingsRestoreCandidate> candidates, string headerText)
    {
        foreach (var c in candidates) Candidates.Add(c);
        HeaderText = headerText;
        InitializeComponent();
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
