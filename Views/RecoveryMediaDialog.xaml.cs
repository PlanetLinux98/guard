using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Stepped wizard that builds a bootable Windows installation USB (detect ->
// choose ISO -> choose USB -> confirm erase -> build -> result). Navigation is
// driven by the dialog's Primary/Secondary buttons with args.Cancel = true, so
// the dialog never auto-closes until the user is done. The destructive build
// runs elevated; its progress is tailed from a log because output can't cross the
// elevation boundary.
public sealed partial class RecoveryMediaDialog : ContentDialog
{
    public nint WindowHandle { get; set; }

    private int _step;
    private readonly string _arch;
    private readonly int _major;
    private string? _isoPath;
    private bool _building;
    private bool _opened;
    private bool _cancelRequested;
    private bool _buildCancelled;
    private LogTail? _buildTail;
    private string? _buildError;

    public RecoveryMediaDialog()
    {
        InitializeComponent();
        _arch = RecoveryMedia.DetectArchitecture();
        _major = RecoveryMedia.DetectWindowsMajor();
        DetectText.Text = "This USB is used to start the recovery tools and restore your system image.\n\n" +
            "This PC is Windows " + _major + ", " + _arch + ", so use a Windows " + _major + " " + _arch + " installation ISO.";
        // Alt mnemonics on the dialog buttons. Back/Cancel are constant letters, so
        // a Style carries them; the primary changes (Next / Erase and Build) and is
        // restyled as the default button, so its key is set on the realized button.
        SecondaryButtonStyle = UiHelpers.AccessKeyButtonStyle("B");
        CloseButtonStyle = UiHelpers.AccessKeyButtonStyle("C");
        // A screen reader does not auto-read content that swaps inside an open
        // dialog (no focus move, no live region), so announce each step's text as
        // it appears. Opened fires the first one once the dialog is on screen.
        Opened += (_, _) => { _opened = true; ApplyPrimaryAccessKey(); UpdateActionButtonDescriptions(); AnnounceStep(); };
        ShowStep(0);
    }

    private void ApplyPrimaryAccessKey()
    {
        string key = _step == 3 ? "E" : "N";   // Erase and Build / Next
        if (UiHelpers.FindDescendantByName(this, "PrimaryButton") is Button b) b.AccessKey = key;
    }

    private void ShowStep(int step)
    {
        _step = step;
        Step0.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        // Empty button text hides that ContentDialog button.
        switch (step)
        {
            case 0:
                PrimaryButtonText = "Next"; SecondaryButtonText = ""; CloseButtonText = "Cancel";
                IsPrimaryButtonEnabled = true;
                break;
            case 1:
                PrimaryButtonText = "Next"; SecondaryButtonText = "Back"; CloseButtonText = "Cancel";
                IsPrimaryButtonEnabled = _isoPath != null && File.Exists(_isoPath);
                break;
            case 2:
                PrimaryButtonText = "Next"; SecondaryButtonText = "Back"; CloseButtonText = "Cancel";
                IsPrimaryButtonEnabled = SelectedUsb() != null;
                break;
            case 3:
                PrimaryButtonText = "Erase and Build"; SecondaryButtonText = "Back"; CloseButtonText = "Cancel";
                IsPrimaryButtonEnabled = true;
                break;
            case 4:
                // Cancel stays available during the build (handled in OnClose).
                PrimaryButtonText = ""; SecondaryButtonText = ""; CloseButtonText = "Cancel";
                break;
            case 5:
                PrimaryButtonText = ""; SecondaryButtonText = ""; CloseButtonText = "Close";
                break;
        }
        if (_opened) { ApplyPrimaryAccessKey(); UpdateActionButtonDescriptions(); AnnounceStep(); }
    }

    // The confirm warning and the result text are set before their panel is shown,
    // so a live region won't fire and the in-dialog notification is unreliable.
    // Attach them as the focused action button's description instead, which a
    // screen reader speaks on focus (focus lands on that button on these steps).
    private void UpdateActionButtonDescriptions()
    {
        if (UiHelpers.FindDescendantByName(this, "PrimaryButton") is Button pb)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(pb, _step == 3 ? ConfirmText.Text : "");
        if (UiHelpers.FindDescendantByName(this, "CloseButton") is Button cb)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(cb, _step == 5 ? ResultText.Text : "");
    }

    // The narration for the current step (the heading plus the key body text), so
    // a screen reader reads each page as it appears.
    private string StepNarration() => _step switch
    {
        0 => DetectText.Text,
        1 => "Choose a Windows installation ISO. Browse to an ISO file you have, or press Get the official ISO from Microsoft.",
        2 => "Choose the USB drive to use. Only removable USB drives are listed. Warning: the drive you pick will be completely erased.",
        // 3 and 5 are read via the action button's HelpText on focus instead.
        3 => "",
        4 => "Building the bootable USB. This can take several minutes. Do not remove the drive until it finishes.",
        5 => "",
        _ => "",
    };

    // Raise the announcement on the dialog itself (always present in the UIA tree;
    // the step panels toggle visibility, and a collapsed element's notification is
    // dropped). A short delay lets the dialog's own open/focus announcement clear.
    private async void AnnounceStep()
    {
        string text = StepNarration();
        if (string.IsNullOrWhiteSpace(text)) return;
        await Task.Delay(500);
        try
        {
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(this)
                       ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseNotificationEvent(
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other,
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                text, "GuardWizardStep");
        }
        catch { }
    }

    // Set the build status text and force its announcement: inside a ContentDialog
    // popup the automatic LiveRegionChanged event often doesn't fire, so a screen
    // reader otherwise stays silent as progress updates.
    private void SetBuildStatus(string text)
    {
        BuildStatus.Text = text;
        try
        {
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(BuildStatus)
                ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
        catch { }
    }

    private RecoveryMedia.UsbDisk? SelectedUsb() =>
        (UsbList.SelectedItem as ListViewItem)?.Tag as RecoveryMedia.UsbDisk;

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Drive navigation ourselves; never let the primary button close the dialog.
        args.Cancel = true;
        var def = args.GetDeferral();
        try
        {
            if (_step == 0)
            {
                ShowStep(1);
            }
            else if (_step == 1)
            {
                if (_isoPath != null && File.Exists(_isoPath))
                {
                    await PopulateUsb();
                    ShowStep(2);
                }
            }
            else if (_step == 2)
            {
                if (SelectedUsb() != null) { PrepareConfirm(); ShowStep(3); }
            }
            else if (_step == 3)
            {
                ShowStep(4);
                await RunBuild();
                ShowStep(5);
            }
        }
        finally { def.Complete(); }
    }

    private void OnSecondary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step == 1) ShowStep(0);
        else if (_step == 2) ShowStep(1);
        else if (_step == 3) ShowStep(2);
    }

    private void OnClose(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // During the build, Cancel doesn't close: it asks the elevated script to
        // stop at the next stage (the script can't be killed from here). The dialog
        // stays open and moves to the result once the script aborts.
        if (_building)
        {
            args.Cancel = true;
            if (!_cancelRequested)
            {
                _cancelRequested = true;
                try { File.WriteAllText(GuardPaths.RecoveryMediaCancelPath, "cancel"); } catch { }
                SetBuildStatus("Stopping...");
            }
        }
    }

    private async void OnBrowseIso(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add(".iso");
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _isoPath = file.Path;
            TxtIso.Text = file.Path;
            IsPrimaryButtonEnabled = true;
        }
    }

    private void OnGetIso(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RecoveryMedia.DownloadPageUrl(_major)) { UseShellExecute = true }); }
        catch { }
    }

    private async void OnRefreshUsb(object sender, RoutedEventArgs e) => await PopulateUsb();

    private void OnUsbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_step == 2) IsPrimaryButtonEnabled = SelectedUsb() != null;
    }

    private async Task PopulateUsb()
    {
        UsbList.Items.Clear();
        var disks = await RecoveryMedia.EnumerateRemovableDrivesAsync();
        foreach (var d in disks)
            UsbList.Items.Add(new ListViewItem
            {
                Content = d.Model + "  -  " + SaveValidation.FormatBytes(d.SizeBytes) + "  (Disk " + d.Number + ")",
                Tag = d,
            });
        UsbEmpty.Visibility = disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = SelectedUsb() != null;
    }

    private void PrepareConfirm()
    {
        var usb = SelectedUsb();
        if (usb == null) return;
        ConfirmText.Text = "This will ERASE EVERYTHING on:\n\n" +
            usb.Model + "  -  " + SaveValidation.FormatBytes(usb.SizeBytes) + "  (Disk " + usb.Number + ")\n\n" +
            "All data on that drive will be permanently deleted. Make sure this is the right drive and that anything important on it is backed up elsewhere.";
    }

    private async Task RunBuild()
    {
        var usb = SelectedUsb();
        if (usb == null || _isoPath == null) return;
        _building = true;
        _cancelRequested = false;
        _buildCancelled = false;
        _buildError = null;
        try { File.Delete(GuardPaths.RecoveryMediaCancelPath); } catch { }
        // Clear any prior run's log so the tail doesn't briefly show a stale error
        // before the elevated script truncates and rewrites it.
        try { File.WriteAllText(GuardPaths.RecoveryMediaLogPath, ""); } catch { }
        _buildTail = new LogTail(GuardPaths.RecoveryMediaLogPath, startAtEnd: false);
        BuildBar.IsIndeterminate = true;
        BuildBar.Value = 0;
        SetBuildStatus("Preparing...");

        string script = RecoveryMedia.BuildUsbScript(
            usb.Number, _isoPath, GuardPaths.RecoveryMediaLogPath, GuardPaths.RecoveryMediaCancelPath);
        string? err = null;
        bool ok = false;
        try
        {
            var runTask = Task.Run(() => ProcessRunner.RunPowerShellElevated(script, out err));
            while (!runTask.IsCompleted)
            {
                await Task.Delay(700);
                PumpBuildLog();
            }
            PumpBuildLog();
            ok = await runTask;
        }
        catch (Exception ex) { err = ex.Message; }

        _building = false;
        BuildBar.IsIndeterminate = false;
        if (ok) BuildBar.Value = 100;
        try { File.Delete(GuardPaths.RecoveryMediaCancelPath); } catch { }
        if (ok)
        {
            ResultHeading.Text = "Finished";
            ResultText.Text = "Your bootable recovery USB is ready.\n\n" +
                "To restore later: start the PC from this USB, choose 'Repair your computer', then Troubleshoot, Advanced options, System Image Recovery.";
        }
        else if (_buildCancelled || _cancelRequested)
        {
            ResultHeading.Text = "Stopped";
            ResultText.Text = "Build cancelled. The USB drive was partly written and is not bootable; run the wizard again to retry.";
        }
        else if (err != null && err.Contains("declined"))
        {
            ResultHeading.Text = "Cancelled";
            ResultText.Text = "Cancelled - Administrator approval was declined. The USB drive was not changed.";
        }
        else
        {
            ResultHeading.Text = "Could not finish";
            ResultText.Text = "The recovery USB could not be built."
                + (_buildError != null ? "\n\n" + _buildError : "")
                + "\n\nSee the log for details:\n" + GuardPaths.RecoveryMediaLogPath;
        }
    }

    // Tail the elevated build log for the latest status line; capture any ERROR:
    // line to explain a failure. LogTail handles offsets and partial lines (an
    // ERROR: line split across two polls used to lose the failure reason).
    private void PumpBuildLog()
    {
        if (_buildTail == null) return;
        foreach (var raw in _buildTail.ReadNewLines())
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("@@PCT@@"))
            {
                if (int.TryParse(line.Substring("@@PCT@@".Length).Trim(), out int pct))
                {
                    BuildBar.IsIndeterminate = false;
                    BuildBar.Value = pct;
                }
                continue;
            }
            if (line.StartsWith("CANCELLED", StringComparison.OrdinalIgnoreCase)) { _buildCancelled = true; continue; }
            if (line.StartsWith("FINISHED OK")) continue;
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) _buildError = line;
            SetBuildStatus(line);
        }
    }
}
