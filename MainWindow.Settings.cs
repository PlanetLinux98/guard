using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3;

// Settings page (_activePage == 3, reached via the NavigationView's built-in
// Settings footer item) and the updater flows it configures. Preferences live
// in guard-prefs.ini and persist the moment they change; there is no Save
// button and no dirty state.
public sealed partial class MainWindow
{
    private AppPrefs _prefs = new();
    // Suppress the change handlers while the controls seed. Starts TRUE, not
    // false: RbThemeSystem is IsChecked="True" in XAML, so its Checked event
    // fires DURING InitializeComponent, when its sibling radios are still null;
    // an unguarded handler there null-crashes across the WinRT ABI (the
    // 0xc000027b fail-fast). InitializeSettingsPage clears it once seeded.
    private bool _prefsLoading = true;
    // Staged update (apply script path) launched by OnWindowClosed; set by
    // Install and Relaunch or by the silent install-on-exit download.
    private string? _pendingUpdateScript;
    private GitHubRelease? _availableRelease;
    private bool _updateCheckBusy;

    private void InitializeSettingsPage()
    {
        _prefs = AppPrefsStore.Load();
        _prefsLoading = true;
        ChkUpdateAutoCheck.IsChecked = _prefs.UpdateAutoCheck;
        ChkUpdateAutoInstall.IsChecked = _prefs.UpdateAutoInstall;
        ChkNotifyFailure.IsChecked = _prefs.NotifyFailure;
        ChkNotifySuccess.IsChecked = _prefs.NotifySuccess;
        RbThemeLight.IsChecked = _prefs.Theme == "Light";
        RbThemeDark.IsChecked = _prefs.Theme == "Dark";
        RbThemeSystem.IsChecked = _prefs.Theme != "Light" && _prefs.Theme != "Dark";
        PopulateStartupCombo();
        UpdateAutoInstallEnabledState();
        _prefsLoading = false;
        ApplyTheme();
    }

    // The root element's RequestedTheme overrides the OS theme for the whole
    // tree; Default hands control back to the OS (the pre-Settings behaviour).
    // The Mica backdrop follows the content theme on its own; dialogs do not
    // (popup layer), so ShowDialogAsync mirrors this onto each dialog.
    private void ApplyTheme()
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = _prefs.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
    }

    // The combo mirrors the nav's own menu items (label from the item's
    // accessible name, value from its Tag), so a future page shows up here with
    // no extra work. Runs once at startup; falls back to the first page when
    // the saved tag no longer matches anything (a renamed or removed page).
    private void PopulateStartupCombo()
    {
        CmbStartupPage.Items.Clear();
        int selected = 0, i = 0;
        foreach (var obj in Nav.MenuItems)
        {
            if (obj is not NavigationViewItem item || item.Tag is not string tag) continue;
            string label = AutomationProperties.GetName(item);
            if (string.IsNullOrEmpty(label)) label = item.Content as string ?? tag;
            CmbStartupPage.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
            if (tag == _prefs.StartupPage) selected = i;
            i++;
        }
        CmbStartupPage.SelectedIndex = selected;
    }

    // Deferred out of the constructor (see the TryEnqueue there): selecting a
    // page triggers its lazy work (app scan, wbadmin probe), which must not run
    // before the visual tree is live.
    private void ApplyStartupPage()
    {
        if (_prefs.StartupPage == "file") return; // the XAML default selection
        foreach (var obj in Nav.MenuItems)
            if (obj is NavigationViewItem item && item.Tag as string == _prefs.StartupPage)
            {
                Nav.SelectedItem = item;
                return;
            }
        // Stale tag: File Backup stays selected.
    }

    private void OnUpdatePrefChanged(object sender, RoutedEventArgs e)
    {
        if (_prefsLoading) return;
        _prefs.UpdateAutoCheck = ChkUpdateAutoCheck.IsChecked == true;
        _prefs.UpdateAutoInstall = ChkUpdateAutoInstall.IsChecked == true;
        UpdateAutoInstallEnabledState();
        AppPrefsStore.Save(_prefs);
    }

    // Auto-install rides on the auto-check (no check, nothing to install), so it
    // greys out while auto-check is off, like the schedule sections' dependents.
    private void UpdateAutoInstallEnabledState()
    {
        if (ChkUpdateAutoInstall == null) return;
        // Also off for a winget install, where GUARD never self-installs (see
        // ShowUpdateDialogAsync). A checkbox that ticks, persists and does
        // nothing is the same silent-lie bug class this whole branch is about,
        // so it is disabled and says why rather than quietly ignored.
        bool winget = GuardPaths.IsWingetManaged;
        ChkUpdateAutoInstall.IsEnabled = !winget && ChkUpdateAutoCheck.IsChecked == true;
        ToolTipService.SetToolTip(ChkUpdateAutoInstall, winget
            ? "Not available for a winget install: update with \"winget upgrade PlanetLinux98.GUARD\""
            : "When a check finds a new version, download it in the background and apply it after you close GUARD, instead of asking first");
    }

    // Read by the headless helper (HeadlessBackupRunner) at its next run; no
    // live plumbing needed beyond persisting the preference.
    private void OnNotifyPrefChanged(object sender, RoutedEventArgs e)
    {
        if (_prefsLoading) return;
        _prefs.NotifyFailure = ChkNotifyFailure.IsChecked == true;
        _prefs.NotifySuccess = ChkNotifySuccess.IsChecked == true;
        AppPrefsStore.Save(_prefs);
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_prefsLoading) return;
        _prefs.Theme = RbThemeLight.IsChecked == true ? "Light"
                     : RbThemeDark.IsChecked == true ? "Dark" : "System";
        AppPrefsStore.Save(_prefs);
        ApplyTheme();
    }

    private void OnStartupPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_prefsLoading) return;
        if (CmbStartupPage.SelectedItem is ComboBoxItem it && it.Tag is string tag)
        {
            _prefs.StartupPage = tag;
            AppPrefsStore.Save(_prefs);
        }
    }

    // =====================================================================
    //  SCHEDULED TASK REMOVAL
    // =====================================================================

    // The deliberate "I am about to remove GUARD" exit; see ScheduledTasks.RemoveAll
    // for why it has to exist separately from turning the schedules off.
    // Reentrancy is held off with a flag rather than by disabling the button:
    // the confirm dialog hands focus back to it, and disabling a focused control
    // lets WinUI throw focus at an arbitrary neighbour, which a screen reader
    // announces over whatever it was saying (see BeginRunBusy for the same
    // problem on the run buttons).
    private bool _removingTasks;

    private async void OnRemoveScheduledTasks(object sender, RoutedEventArgs e)
    {
        if (_removingTasks) return;
        if (!await ShowConfirmAsync("GUARD",
            "Remove GUARD's scheduled tasks from Windows?\n\n"
            + "This unregisters the scheduled backup, the on-connect check, and the scheduled system"
            + " image, and switches those schedules off in your settings. Your generated scripts and"
            + " your existing backups are not touched, and you can switch the schedules back on at any"
            + " time with Save Settings.\n\n"
            + "Do this before deleting or uninstalling GUARD: the tasks are registered with Windows, so"
            + " they would otherwise keep firing at an app that is no longer there.",
            "Remove", "Cancel")) return;

        _removingTasks = true;
        try
        {
            // Spoken, not just shown: the batched PowerShell call takes seconds
            // and the button stays enabled, so without this there is no cue that
            // anything is happening.
            AnnounceNotification("Removing GUARD's scheduled tasks...");
            var result = await System.Threading.Tasks.Task.Run(ScheduledTasks.RemoveAll);
            if (result.Error != null)
            {
                await ShowMessageAsync("GUARD", "Could not remove the scheduled tasks:\n\n" + result.Error
                    + "\n\nGUARD's settings have been left as they were.");
                return;
            }
            if (result.Remaining.Count > 0)
            {
                // Settings deliberately NOT changed: switching the schedules off
                // here would stop GUARD ever re-registering or healing a task
                // that is demonstrably still in Windows.
                await ShowMessageAsync("GUARD",
                    "These scheduled tasks could not be removed:\n\n" + string.Join("\n", result.Remaining)
                    + "\n\nThey may belong to another user account, or Windows may have refused the change."
                    + " GUARD's settings have been left as they were, so you can try again.");
                return;
            }

            // Only asked when one is actually registered, so nobody who never
            // scheduled an image has to answer a UAC prompt.
            string? imageError = null;
            bool imageRemoved = false;
            if (result.ImageTaskRemains)
            {
                if (await ShowConfirmAsync("GUARD",
                    "The backup tasks are gone. A scheduled system image is also registered.\n\n"
                    + "It runs as the system account, so removing it needs Administrator approval.\n\n"
                    + "Remove it as well?", "Remove", "Leave it"))
                {
                    imageError = await System.Threading.Tasks.Task.Run(ScheduledTasks.RemoveSystemImageTask);
                    imageRemoved = imageError == null;
                }
            }

            // The image schedule is only switched off when its task is actually
            // gone. Left on when the removal failed or was declined, so GUARD and
            // Windows still agree about a task that is still going to fire.
            ClearScheduleStateAfterRemoval(alsoImage: imageRemoved || !result.ImageTaskRemains);

            await ShowMessageAsync("GUARD", imageError != null
                ? "The backup tasks were removed. " + imageError
                : "GUARD's scheduled tasks have been removed from Windows.");
        }
        finally { _removingTasks = false; }
    }

    // Bring the saved settings and the on-screen controls in line with what was
    // just unregistered, so nothing offers to "re-register" a task the user
    // deliberately removed.
    //
    // The on-disk settings are re-read and flipped rather than written from the
    // live config: _cfg carries the File Backup page's two-way-bound folder and
    // exclusion edits, so saving it here would silently commit changes the user
    // never pressed Save for (the reason SettingsStore's saves are section
    // scoped). The dirty flags are restored, not cleared, for the same reason -
    // unticking the boxes below sets them, but any edit that was already
    // pending is still pending.
    private void ClearScheduleStateAfterRemoval(bool alsoImage)
    {
        bool wasDirty = _dirty, wasImageDirty = _imageDirty;
        try
        {
            var onDisk = SettingsStore.Load();
            onDisk.ScheduleEnabled = false;
            onDisk.TriggerOnConnect = false;
            if (alsoImage) onDisk.ImageScheduleEnabled = false;
            SettingsStore.Save(onDisk);
        }
        catch (Exception ex) { DebugLog.Log("tasks", "could not persist the schedule-off state", ex); }

        ChkSchedule.IsChecked = false;
        ChkOnConnect.IsChecked = false;
        _cfg.ScheduleEnabled = false;
        _cfg.TriggerOnConnect = false;
        LblNextRun.Text = "Next run: (no scheduled task)";
        if (alsoImage)
        {
            ChkImageSchedule.IsChecked = false;
            _cfg.ImageScheduleEnabled = false;
            _imageTaskStale = false;
            _lastImageScheduleSig = ImageScheduleSignature(_cfg);
            LblImageNextRun.Text = "Next run: (no scheduled image)";
        }
        _dirty = wasDirty;
        _imageDirty = wasImageDirty;
        RefreshScriptStatus(announce: false);
        RefreshImageStatus(announce: false);
    }

    // =====================================================================
    //  UPDATE CHECKS
    // =====================================================================

    // Launch auto-check: at most once a day, silent on every no-news path (off,
    // already checked today, offline, up to date, or the offered version was
    // skipped). Finding one either notifies (InfoBar + announcement; the update
    // dialog is a click away) or, in auto-install mode, downloads in the
    // background and applies on exit.
    private async System.Threading.Tasks.Task AutoUpdateCheckAsync()
    {
        // Nothing is staged yet this session, so any leftover staging folder
        // is a previous session's (applied or abandoned); clear it before any
        // new stage can begin. Awaited so a fast auto-install download can
        // never race the deletion.
        await System.Threading.Tasks.Task.Run(Updater.CleanupStage);
        if (!_prefs.UpdateAutoCheck) return;
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_prefs.LastUpdateCheck == today) return;
        var rel = await Updater.FetchLatestAsync();
        // Offline or API trouble: don't stamp the day, so the next launch retries.
        if (rel is null) return;
        _prefs.LastUpdateCheck = today;
        AppPrefsStore.Save(_prefs);
        if (!Updater.IsNewer(rel.TagName)) return;
        if (rel.TagName == _prefs.SkippedVersion) return;
        _availableRelease = rel;

        // Never stage a silent self-install over a winget-managed folder; the
        // InfoBar below still announces the release, and the dialog explains the
        // winget route.
        if (_prefs.UpdateAutoInstall && !GuardPaths.IsWingetManaged && Updater.BaseDirWritable())
        {
            try
            {
                _pendingUpdateScript = await Updater.DownloadAndStageAsync(
                    rel, relaunch: false, progress: null, CancellationToken.None);
                ShowUpdateInfoBar("GUARD " + rel.TagName +
                    " has been downloaded and will be installed when you exit GUARD.",
                    showAction: false);
                return;
            }
            catch (Exception ex)
            {
                // Background download failed; fall back to the notify-only offer
                // (the dialog's download surfaces its errors to the user).
                DebugLog.Log("updater", "silent auto-install stage failed", ex);
            }
        }
        ShowUpdateInfoBar("GUARD " + rel.TagName + " is available.", showAction: true);
    }

    private void ShowUpdateInfoBar(string message, bool showAction)
    {
        UpdateInfoBar.Title = "Update available";
        UpdateInfoBar.Message = message;
        BtnUpdateDetails.Visibility = showAction ? Visibility.Visible : Visibility.Collapsed;
        UpdateInfoBar.IsOpen = true;
        // Same settle delay as job announcements: launch focus churn would
        // otherwise cut the speech off.
        AnnounceSettled(message + (showAction
            ? " Press Control+U to see what's new and choose whether to install."
            : ""), 2000);
    }

    private async void OnUpdateInfoBarAction(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is not null) await ShowUpdateDialogAsync(_availableRelease);
    }

    private async void OnCheckUpdatesNow(object sender, RoutedEventArgs e)
        => await CheckForUpdatesNowAsync();

    // Manual check (Settings button or About): always reports a result, and
    // ignores the skipped version - asking again IS reconsidering the skip.
    private async System.Threading.Tasks.Task CheckForUpdatesNowAsync()
    {
        if (_updateCheckBusy) return;
        _updateCheckBusy = true;
        BtnCheckUpdates.IsEnabled = false;
        try
        {
            var rel = await Updater.FetchLatestAsync();
            if (rel is null)
            {
                await ShowMessageAsync("GUARD",
                    "Could not check for updates. Check your internet connection and try again.");
                return;
            }
            _prefs.LastUpdateCheck = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            AppPrefsStore.Save(_prefs);
            if (!Updater.IsNewer(rel.TagName))
            {
                await ShowMessageAsync("GUARD",
                    "You are up to date. GUARD " + GuardPaths.AppVersion + " is the latest version.");
                return;
            }
            _availableRelease = rel;
            await ShowUpdateDialogAsync(rel);
        }
        finally
        {
            _updateCheckBusy = false;
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task ShowUpdateDialogAsync(GitHubRelease rel)
    {
        // A winget install belongs to winget. Self-updating would extract over
        // the package folder behind winget's back, leaving its recorded version
        // stale - and the next `winget upgrade` would then delete the folder and
        // reinstall anyway. Point at the supported route instead.
        if (GuardPaths.IsWingetManaged)
        {
            await ShowMessageAsync("GUARD",
                "GUARD " + rel.TagName + " is available.\n\nThis copy was installed with winget, so"
                + " update it the same way:\n\n    winget upgrade PlanetLinux98.GUARD\n\nYour settings"
                + " are kept outside the install folder, so they survive the upgrade.");
            return;
        }
        // The apply script rewrites the install folder, so self-update needs it
        // writable; when it isn't, be honest and point at the Releases page.
        if (!Updater.BaseDirWritable())
        {
            bool open = await ShowConfirmAsync("GUARD",
                "GUARD " + rel.TagName + " is available, but GUARD's folder is not writable" +
                " from here, so it cannot update itself. Download GUARD.zip from the" +
                " project's Releases page and replace this folder's files by hand.\n\n" +
                "Open the Releases page now?");
            if (open)
                try { Process.Start(new ProcessStartInfo(rel.HtmlUrl) { UseShellExecute = true }); } catch { }
            return;
        }

        var dlg = new Views.UpdateDialog(rel) { XamlRoot = Content.XamlRoot };
        await ShowDialogAsync(dlg);
        if (dlg.SkipRequested)
        {
            _prefs.SkippedVersion = rel.TagName;
            AppPrefsStore.Save(_prefs);
            // Skip must also cancel a silently staged install-on-exit for this
            // same version, or the version the user just skipped would apply
            // the moment they close GUARD anyway.
            _pendingUpdateScript = null;
            UpdateInfoBar.IsOpen = false;
        }
        else if (dlg.StagedScript is string script)
        {
            _pendingUpdateScript = script;
            UpdateInfoBar.IsOpen = false;
            // Normal closing flow: the unsaved-changes prompt and the
            // running-job confirmation still apply. If the user cancels the
            // close, the staged update simply applies on whichever exit
            // actually happens (and relaunches, as they asked).
            Close();
        }
        else if (dlg.DownloadAttempted && _pendingUpdateScript is not null)
        {
            // Install was tried and failed (or was cancelled). Staging starts
            // by wiping the staging folder, so an earlier auto-staged script
            // no longer exists; drop it and retract the install-on-exit
            // promise the InfoBar made. Tomorrow's auto-check re-stages.
            _pendingUpdateScript = null;
            UpdateInfoBar.IsOpen = false;
        }
        // Remind Me Later: nothing to do; tomorrow's auto-check re-offers.
    }

    // Window.Closed: the process is about to end, so the staged script's
    // wait-for-PID loop clears almost immediately.
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_pendingUpdateScript is string script)
        {
            try { Updater.LaunchApplier(script); }
            catch (Exception ex) { DebugLog.Log("updater", "could not launch the staged apply script", ex); }
            _pendingUpdateScript = null;
        }
    }
}
