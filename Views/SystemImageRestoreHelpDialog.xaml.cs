using System;
using System.Diagnostics;
using GuardWui3.Services;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

public sealed partial class SystemImageRestoreHelpDialog : ContentDialog
{
    readonly string _target;

    // imageTarget is the saved destination. A share's server name is resolved to an
    // IP separately via SetResolvedIp, on a background thread, so a slow or failing
    // DNS lookup never delays the dialog opening; until it lands (or if it fails)
    // the name-based guidance below stands.
    public SystemImageRestoreHelpDialog(string? imageTarget = null)
    {
        InitializeComponent();
        _target = (imageTarget ?? "").Trim();
        if (_target.StartsWith(@"\\"))
        {
            NetworkPathText.Text = "Your image is saved at " + _target
                + ". Enter that path in the recovery tool; if it cannot find the server by name, replace the name with the server's IP address.";
            NetworkPathText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
    }

    // winreNetworkPath is the share's path with the server name swapped for its IP
    // (null when not a share, already an IP, or unresolvable). Call on the UI thread
    // after a background resolve; null leaves the name-based guidance in place.
    public void SetResolvedIp(string? winreNetworkPath)
    {
        if (winreNetworkPath == null || !_target.StartsWith(@"\\")) return;
        NetworkPathText.Text = "Your image is saved at " + _target
            + ". In the recovery tool, enter the IP form instead: " + winreNetworkPath
            + " (same path, with the server's IP in place of its name).";
    }

    private async void OnOpenManual(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open; opening the manual is a side action, not a dismiss.
        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            string target = System.IO.File.Exists(GuardPaths.ManualPath)
                ? GuardPaths.ManualPath : GuardPaths.RepoUrl;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            var msg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Title,
                Content = "Could not open the manual:\n\n" + ex.Message,
                CloseButtonText = "OK"
            };
            await msg.ShowAsync();
        }
        deferral.Complete();
    }
}
