using System;
using System.Threading;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Offers to install winget and, on Install, runs the whole job in-dialog
// (fetch the latest winget-cli release, download with progress, per-user
// Add-AppxPackage, verify) so every entry point - the scan InfoBar, the
// Settings card, the reinstall gate - shows progress in the same place.
// The caller reads Installed after ShowAsync returns and drives any rescan
// or follow-on reinstall itself.
public sealed partial class WingetInstallDialog : ContentDialog
{
    private CancellationTokenSource? _cts;
    private bool _busy;
    // The Add-AppxPackage phase: past the point of useful cancellation.
    private bool _installing;

    public bool Installed { get; private set; }

    // contextLine: an optional first line naming why the dialog appeared (the
    // reinstall gate says how many ticked apps need winget); null elsewhere.
    public WingetInstallDialog(string? contextLine)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(contextLine))
        {
            ContextText.Text = contextLine;
            ContextText.Visibility = Visibility.Visible;
        }
        // Mnemonics, same route as UpdateDialog: close via a Style; the default
        // (primary) button's Style is overwritten by the dialog's visual state,
        // so its key goes on the realized button.
        CloseButtonStyle = UiHelpers.AccessKeyButtonStyle("C");
        Opened += (_, _) =>
        {
            if (UiHelpers.FindDescendantByName(this, "PrimaryButton") is { } primary)
                primary.AccessKey = "I";
        };
    }

    private async void OnInstallClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;               // keep the dialog up while it works
        if (_busy) return;
        _busy = true;
        IsPrimaryButtonEnabled = false;   // Cancel stays enabled: it stops the download
        _cts = new CancellationTokenSource();
        try
        {
            SetStatus("Finding the latest winget release...");
            var rel = await WingetBootstrap.FetchLatestAsync(_cts.Token);
            if (rel is null)
            {
                SetStatus("Could not reach GitHub to find the winget download. Check your internet connection and try again.");
                ResetAfterFailure();
                return;
            }

            long mb = WingetBootstrap.DownloadSizeBytes(rel) / (1024 * 1024);
            SetStatus("Downloading winget " + rel.TagName + (mb > 0 ? " (" + mb + " MB)..." : "..."));
            InstallBar.Visibility = Visibility.Visible;
            var prog = new Progress<double>(v => InstallBar.Value = v);
            var payload = await WingetBootstrap.DownloadAsync(rel, prog, _cts.Token);

            _installing = true;
            InstallBar.IsIndeterminate = true;
            SetStatus("Installing winget. This can take a minute or two...");
            await System.Threading.Tasks.Task.Run(() => WingetBootstrap.InstallPayload(payload));
            Installed = true;
            Hide();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Install cancelled. Nothing was changed.");
            ResetAfterFailure();
        }
        catch (Exception ex)
        {
            SetStatus("winget could not be installed: " + ex.Message);
            ResetAfterFailure();
        }
        finally
        {
            _installing = false;
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            // Fire-and-forget: staging cleanup must not hold the dialog open.
            _ = System.Threading.Tasks.Task.Run(WingetBootstrap.Cleanup);
        }
    }

    // Set the status line and force its announcement: inside a ContentDialog
    // popup the automatic LiveRegionChanged event often doesn't fire (same fix
    // as UpdateDialog / RecoveryMediaDialog), so a screen reader otherwise
    // stays silent on progress and errors.
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(StatusText)
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch { }
    }

    private void ResetAfterFailure()
    {
        IsPrimaryButtonEnabled = true;
        InstallBar.Visibility = Visibility.Collapsed;
        InstallBar.IsIndeterminate = false;
        InstallBar.Value = 0;
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Past the download, the deployment is committing; holding the dialog
        // beats killing a transactional install for a half-state.
        if (_installing)
        {
            args.Cancel = true;
            SetStatus("winget is being installed now; this step cannot be cancelled. It only takes a minute or two.");
            return;
        }
        // Mid-download, Cancel (or Esc) means "stop downloading", not "dismiss":
        // cancel and keep the dialog up so the outcome is visible; a second
        // press then dismisses normally.
        if (_busy)
        {
            args.Cancel = true;
            _cts?.Cancel();
        }
    }
}
