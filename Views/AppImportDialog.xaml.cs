using System.Collections.Generic;
using System.Collections.ObjectModel;
using GuardWui3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Shown when the user imports a saved app list. The imported apps live here, in
// their own tickable list, rather than replacing the installed-apps list behind
// the tab - so "what is on this PC now" (for Export) and "what was saved" (to
// reinstall) never get conflated. This dialog is selection only: the chosen
// action closes it and the actual reinstall runs on the main window, which
// already has the progress, output console and Stop button.
//
//   Primary  ("Reinstall Selected")            -> reinstall the ticked apps
//   Secondary("Reinstall & Restore Settings")  -> reinstall, then restore settings
//   Close    ("Cancel")                        -> back to the installed-apps list
//
// Secondary is enabled only when a settings bundle was found beside the list.
public sealed partial class AppImportDialog : ContentDialog
{
    // Bound by x:Bind in the XAML.
    public ObservableCollection<AppEntry> Apps { get; } = new();
    public string HeaderText { get; }

    public AppImportDialog(IEnumerable<AppEntry> apps, string headerText, bool settingsBundleFound)
    {
        foreach (var a in apps) Apps.Add(a);
        HeaderText = headerText;
        InitializeComponent();
        IsSecondaryButtonEnabled = settingsBundleFound;
        ListTypeAhead.Attach(AppList, o => ((AppEntry)o).Name);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var a in Apps) a.Include = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var a in Apps) a.Include = false;
    }
}
