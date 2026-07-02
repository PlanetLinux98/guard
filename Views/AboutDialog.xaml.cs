using System;
using System.Diagnostics;
using System.IO;
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
        _ = LoadIconAsync();
    }

    // The logo ships embedded in the assembly (csproj EmbeddedResource), not
    // as a loose file; a decode failure just leaves the image slot empty.
    private async System.Threading.Tasks.Task LoadIconAsync()
    {
        try
        {
            using var res = typeof(AboutDialog).Assembly
                .GetManifestResourceStream("GUARD.Icon256.png");
            if (res is null) return;
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var ras = res.AsRandomAccessStream();
            await bmp.SetSourceAsync(ras);
            IconImage.Source = bmp;
        }
        catch { }
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
