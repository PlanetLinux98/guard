using System;
using System.Threading;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Offers a newer release: Install and Relaunch downloads, verifies and stages
// the update in-dialog (the dialog stays up showing progress), Skip This
// Version is recorded by the caller, and Remind Me Later (also Esc) does
// nothing - the next daily check re-offers. The caller reads StagedScript /
// SkipRequested after ShowAsync returns and drives the close-and-apply.
public sealed partial class UpdateDialog : ContentDialog
{
    private readonly GitHubRelease _release;
    private CancellationTokenSource? _cts;
    private bool _downloading;

    // Path of the staged apply script once Install succeeded, else null.
    public string? StagedScript { get; private set; }
    public bool SkipRequested { get; private set; }

    public UpdateDialog(GitHubRelease release)
    {
        _release = release;
        InitializeComponent();
        HeadlineText.Text = "GUARD " + release.TagName + " is available. You have version "
            + GuardPaths.AppVersion + ".";
        // De-markdowned so NVDA reads the notes as prose, not syntax; the
        // converter emits \n, the TextBox wants \r\n.
        NotesBox.Text = string.IsNullOrWhiteSpace(release.Body)
            ? "(This release has no notes.)"
            : Updater.NotesToPlainText(release.Body).Replace("\n", "\r\n");
        // Mnemonics, same route as the save-on-close prompt: secondary/close via
        // a Style; the default (primary) button's Style is overwritten by the
        // dialog's visual state, so its key goes on the realized button.
        SecondaryButtonStyle = UiHelpers.AccessKeyButtonStyle("S");
        CloseButtonStyle = UiHelpers.AccessKeyButtonStyle("R");
        Opened += (_, _) =>
        {
            if (UiHelpers.FindDescendantByName(this, "PrimaryButton") is { } primary)
                primary.AccessKey = "I";
        };
    }

    private async void OnInstallClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;               // keep the dialog up while downloading
        if (_downloading) return;
        _downloading = true;
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false; // Close stays enabled: it cancels the download
        DownloadBar.Visibility = Visibility.Visible;
        SetStatus("Downloading the update...");
        _cts = new CancellationTokenSource();
        try
        {
            var prog = new Progress<double>(v => DownloadBar.Value = v);
            StagedScript = await Updater.DownloadAndStageAsync(_release, relaunch: true, prog, _cts.Token);
            Hide();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Download cancelled.");
            ResetAfterFailure();
        }
        catch (Exception ex)
        {
            SetStatus("The update could not be downloaded: " + ex.Message);
            ResetAfterFailure();
        }
        finally
        {
            _downloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Set the status line and force its announcement: inside a ContentDialog
    // popup the automatic LiveRegionChanged event often doesn't fire (same fix
    // as RecoveryMediaDialog's build status), so a screen reader otherwise
    // stays silent on download errors.
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
        IsSecondaryButtonEnabled = true;
        DownloadBar.Visibility = Visibility.Collapsed;
        DownloadBar.Value = 0;
    }

    private void OnSkipClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => SkipRequested = true;

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Mid-download, Close (or Esc) means "stop downloading", not "dismiss":
        // cancel and keep the dialog up so the outcome is visible; a second
        // press then dismisses normally.
        if (_downloading)
        {
            args.Cancel = true;
            _cts?.Cancel();
        }
    }
}
