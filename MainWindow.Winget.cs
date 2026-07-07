using System;
using System.Threading;
using GuardWui3.Services;
using Microsoft.UI.Xaml;

namespace GuardWui3;

// winget bootstrap offers: the InfoBar raised by an app scan, the Settings-page
// card (lazy probe, like the wbadmin check), and the shared install dialog all
// three entry points funnel into - one progress surface wherever it starts.
public sealed partial class MainWindow
{
    // True once an app scan or the Settings probe has answered whether winget
    // exists, so the background probe never runs twice.
    private bool _wingetChecked;

    private void ShowWingetOffer()
    {
        // One short line beside the title; the how-and-what detail lives in the
        // action button's tooltip and the install dialog itself.
        WingetInfoBar.Title = "winget not installed";
        WingetInfoBar.Message = "Apps cannot be reinstalled automatically until it is installed.";
        WingetInfoBar.IsOpen = true;
        CardWinget.Visibility = Visibility.Visible;
    }

    private void HideWingetOffer()
    {
        WingetInfoBar.IsOpen = false;
        CardWinget.Visibility = Visibility.Collapsed;
    }

    // Settings can be visited before App Management ever scans, so the card
    // needs its own cheap probe; a scan that has already run supersedes it
    // (ScanApps sets _wingetChecked). Off the UI thread: the probe spawns a
    // process.
    private void ProbeWingetForSettings()
    {
        if (_wingetChecked) return;
        _wingetChecked = true;
        var th = new Thread(() =>
        {
            bool present = WingetBootstrap.Probe();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (present) _wingetAvailable = true;
                else CardWinget.Visibility = Visibility.Visible;
            });
        }) { IsBackground = true };
        th.Start();
    }

    // The standalone offers (InfoBar action, Settings button): install, then
    // announce and refresh the app list when one was already scanned, so the
    // winget ids enrich it. The reinstall gate calls the dialog directly
    // instead - its own start line speaks next, and a rescan would fight the
    // running job for the page's busy state.
    private async void OnWingetOfferInvoked(object sender, RoutedEventArgs e)
    {
        if (!await ShowWingetInstallDialogAsync(null)) return;
        if (_appScanned)
        {
            AnnounceSettled("winget is now installed. Refreshing the app list...", 2000);
            // notifyCompletion: the rescan's summary must be spoken too, and
            // the live region would be dropped this soon after the dialog.
            ScanApps(announceStart: false, notifyCompletion: true);
        }
        else
            AnnounceSettled("winget is now installed.", 2000);
    }

    // Shows the install dialog; true when winget was installed. Announcements
    // and rescans are the caller's business.
    private async System.Threading.Tasks.Task<bool> ShowWingetInstallDialogAsync(string? contextLine)
    {
        var dlg = new Views.WingetInstallDialog(contextLine) { XamlRoot = Content.XamlRoot };
        await ShowDialogAsync(dlg);
        if (!dlg.Installed) return false;
        _wingetAvailable = true;
        _wingetChecked = true;
        HideWingetOffer();
        return true;
    }
}
