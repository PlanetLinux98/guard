using System.Diagnostics;
using GuardWui3.Services;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

public sealed partial class SystemImageRestoreHelpDialog : ContentDialog
{
    // imageTarget is the saved destination; winreNetworkPath is its server name
    // resolved to an IP (null when not a share, already an IP, or unresolvable).
    public SystemImageRestoreHelpDialog(string? imageTarget = null, string? winreNetworkPath = null)
    {
        InitializeComponent();
        string target = (imageTarget ?? "").Trim();
        if (target.StartsWith(@"\\"))
        {
            string toType = winreNetworkPath ?? target;
            NetworkPathText.Text = winreNetworkPath != null
                ? "Your image is saved at " + target + ". In the recovery tool, enter the IP form instead: " + toType
                  + " (same path, with the server's IP in place of its name)."
                : "Your image is saved at " + target + ". Enter that path in the recovery tool; if it cannot find the server by name, replace the name with the server's IP address.";
            NetworkPathText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
    }

    private void OnOpenManual(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            string target = System.IO.File.Exists(GuardPaths.ManualPath)
                ? GuardPaths.ManualPath : GuardPaths.RepoUrl;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }
}
