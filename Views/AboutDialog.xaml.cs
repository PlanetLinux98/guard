using System.Diagnostics;
using GuardWui3.Services;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

public sealed partial class AboutDialog : ContentDialog
{
    // Set when the user picked Check for Updates; MainWindow runs the check
    // after this dialog closes (two ContentDialogs cannot overlap).
    public bool CheckUpdatesRequested { get; private set; }

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = "Version " + GuardPaths.AppVersion;
    }

    private void OnProjectPage(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(GuardPaths.RepoUrl) { UseShellExecute = true }); } catch { }
    }

    private void OnCheckUpdates(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CheckUpdatesRequested = true;
        Hide();
    }
}
