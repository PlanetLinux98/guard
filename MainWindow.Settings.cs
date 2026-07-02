using System;
using System.Diagnostics;
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
        if (ChkUpdateAutoInstall != null)
            ChkUpdateAutoInstall.IsEnabled = ChkUpdateAutoCheck.IsChecked == true;
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
    //  UPDATE CHECKS
    // =====================================================================

    // Launch auto-check: at most once a day, silent on every no-news path (off,
    // already checked today, offline, up to date, or the offered version was
    // skipped). Finding one either notifies (InfoBar + announcement; the update
    // dialog is a click away) or, in auto-install mode, downloads in the
    // background and applies on exit.
    private async System.Threading.Tasks.Task AutoUpdateCheckAsync()
    {
        if (!_prefs.UpdateAutoCheck) return;
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_prefs.LastUpdateCheck == today) return;
        var rel = await Updater.FetchLatestAsync();
        // Offline or API trouble: don't stamp the day, so the next launch retries.
        if (rel is null) return;
        _prefs.LastUpdateCheck = today;
        AppPrefsStore.Save(_prefs);
        if (!Updater.IsNewer(rel.TagName)) return;
        if (rel.TagName == _prefs.SkippedVersion) return;
        _availableRelease = rel;

        if (_prefs.UpdateAutoInstall && Updater.BaseDirWritable())
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
            catch
            {
                // Background download failed; fall back to the notify-only offer
                // (the dialog's download surfaces its errors to the user).
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
            _prefs.LastUpdateCheck = DateTime.Now.ToString("yyyy-MM-dd");
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
        // Remind Me Later: nothing to do; tomorrow's auto-check re-offers.
    }

    // Window.Closed: the process is about to end, so the staged script's
    // wait-for-PID loop clears almost immediately.
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_pendingUpdateScript is string script)
        {
            try { Updater.LaunchApplier(script); } catch { }
            _pendingUpdateScript = null;
        }
    }
}
