using System.Diagnostics;
using GuardWui3.Services;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = "Version " + GuardPaths.AppVersion;
    }

    private void OnProjectPage(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(GuardPaths.RepoUrl) { UseShellExecute = true }); } catch { }
    }
}
