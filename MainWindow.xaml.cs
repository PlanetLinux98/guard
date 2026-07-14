using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GuardWui3;

public sealed partial class MainWindow : Window
{
    private Settings _cfg = new();

    // Cancellation for the two long-running jobs. The Stop buttons only call
    // Cancel(); the process-tree kill hangs off the token (a Register callback),
    // so cancel-vs-natural-exit races collapse to a harmless Kill-after-exit the
    // registration swallows. Both fields are created, cancelled and disposed on
    // the UI thread only.
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reinstallCts;
    private bool _backupRunning;

    private bool _dirty;
    private int _progTotal;

    // Byte-weighted backup progress. When a pre-run scan of the included folders
    // succeeds (_progByBytes), the bar tracks bytes copied / total source bytes:
    // _progOffsets[i] is the cumulative bytes BEFORE folder i, _progSizes[i] its
    // size, so each @@PROGRESS@@ marker snaps the bar to the folder boundary
    // (accounting for skipped files) and robocopy's per-file byte lines move it
    // smoothly within the folder. If the scan fails or is empty the flag stays
    // false and the bar falls back to the per-folder count.
    private bool _progByBytes;
    private long[]? _progSizes;
    private long[]? _progOffsets;
    private long _progTotalBytes;
    private long _curBase, _curSize, _curCopied, _curLastPush, _curPushStep;

    // Per-run robocopy summary accumulation; recreated by RunScript so a stale
    // parser can't leak counts into the next run.
    private RobocopySummaryParser? _summaryParser;
    private bool _runIsPreview;

    // End-of-run summary held back until the run's focus churn settles; see
    // SetFileBusy for why announcing earlier loses the speech.
    private string? _runDoneAnnounce;

    // Save reentrancy guard (the save is async, so the button can be pressed
    // again mid-save) and the staleness counter for the background space/size
    // status check.
    private bool _saving;
    private int _spaceCheckSeq;

    // Only one ContentDialog may be open at a time; WinUI throws otherwise. An
    // access key on the main window (e.g. Alt+R for Remove Folder) still fires
    // while a dialog is up, so guard every show through this flag.
    private bool _dialogOpen;

    // Last text announced on the status live region, so a screen reader is not
    // re-notified on every checkbox toggle when the status text is unchanged.
    private string? _lastAnnouncedStatus;

    // The File Backup settings status, kept here (not only in the status bar
    // text) so switching back from App Management can repaint the bar without
    // recomputing it.
    private string _fileStatusText = "";
    private Brush? _fileStatusBrush;

    // Problem reported by the last scheduled-task registration during a save, or
    // null if it succeeded; surfaced by OnSave after the save completes.
    private string? _taskError;

    // Set by SaveAllAsync when the destination volume was re-found under a new
    // drive letter and the destination was re-anchored to it; OnSave shows it
    // as a dialog, RunScript as an output line.
    private string? _destDriftNote;

    // Included sources that were unreachable at the last save. Advisory only:
    // the generated script SKIPs them at run time, so a save never blocks on
    // them. OnSave folds these into its dialog; RunScript prints them in the
    // output box instead so the run is not interrupted by a modal.
    private List<string> _missingSources = new();

    // Destination/source paths from the last save whose % does not resolve as
    // an environment variable; cmd would silently mangle them in the generated
    // script. Advisory, surfaced the same two ways as _missingSources.
    private List<string> _percentPaths = new();

    // The App Management scan/import summary; the status bar is its only home
    // (an in-place copy under the list was removed as redundant), so it lives
    // here for repainting the bar on tab switches.
    private string _appStatusText = "Open this tab to scan installed apps.";

    private bool _appScanned;
    // Which page the nav has selected (0 = File Backup, 1 = App Management,
    // 2 = System Image); the status bar, its announcements, and the per-page
    // progress array key off this, as they did off the old Pivot's SelectedIndex.
    private int _activePage;
    private bool _scanning;
    private bool _reinstalling;
    private bool _exporting;
    private bool _wingetAvailable;
    private bool _allowClose;

    // ---- System Image page (third page; _activePage == 2) ----
    private bool _imageDirty;
    private bool _imageRunning;
    private bool _imageSaving;
    private bool _imageChecked;            // one-time wbadmin availability probe done
    private bool _imageAvailable = true;   // wbadmin present (false on Home etc.)
    private bool _imageStopRequested;
    private int _imageSpaceSeq;
    private LogTail? _imageTail;
    // wbadmin reports progress per volume; these fold the per-volume percents into
    // one monotonic overall figure so the bar never falsely reaches 100% when an
    // early small volume (the EFI partition) finishes before the next one starts.
    private int _imageTotalVols;
    private int _imageDoneVols;
    private double _imageOverall;
    private string _imageStatusText = "Open this tab to set up full system images.";
    private Brush? _imageStatusBrush;
    private string? _imageTaskError;
    private Control? _imageRunLauncher;
    // The registered SYSTEM image task points at a previous install path (the
    // folder moved); only an elevated save can fix it, so it rides the status
    // line until then. See CheckScheduledTasksAtLaunch.
    private bool _imageTaskStale;
    // Last-applied schedule signature: registering the SYSTEM task needs a UAC
    // prompt, so a save only re-applies (prompts) when one of these changed.
    private string _lastImageScheduleSig = "";
    // Weekly-day combo index order (Monday..Sunday).
    private static readonly DayOfWeek[] _imageDayOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    // The seven schedule-day checkboxes paired with the day each represents.
    private (CheckBox box, DayOfWeek day)[] _dayBoxes = Array.Empty<(CheckBox, DayOfWeek)>();

    private FolderPair? _currentFolder;
    private FrameworkElement? _lastFolderFocus;
    private FrameworkElement? _lastAppFocus;

    // The four exclusion-preset checkboxes paired with the preset id each
    // represents (see ExcludePreset.All).
    private (CheckBox box, string id)[] _presetBoxes = Array.Empty<(CheckBox, string)>();
    private readonly List<AppEntry> _allApps = new();
    private string _appFilter = "";

    // Bound by x:Bind in the XAML.
    public ObservableCollection<FolderPair> Folders => _cfg.Folders;
    public ObservableCollection<ExcludeItem> Excludes => _cfg.Excludes;
    public ObservableCollection<AppEntry> AppRows { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "GUARD";
        SetWindowIcon();
        // Wider than the old 820 to keep the page content roomy beside the
        // ~210 DIP navigation pane (the seven schedule-day checkboxes need a
        // full-width row). Height trimmed from 900 to 840: CenterInWorkArea
        // clamps the top to the work area, but 900 still ran the bottom edge
        // under the taskbar on common smaller-or-scaled displays (e.g. a
        // 1366x768 laptop); the expanders collapse the advanced sections, so
        // the default page has room to spare at the smaller height.
        SizeToDips(1040, 840);
        EnableMinimumWindowSize();

        // Releases up to v0.5.0 shipped the manual as USER_GUIDE.md; an update
        // extracts over BaseDir without deleting old files, so clear the stale
        // copy once the HTML manual is present. Best effort: a locked file
        // just stays until a later launch.
        try
        {
            if (File.Exists(GuardPaths.ManualPath) && File.Exists(GuardPaths.LegacyManualPath))
                File.Delete(GuardPaths.LegacyManualPath);
        }
        catch { }

        _cfg = SettingsStore.Load();

        // Populate the file-tab inputs from settings.
        TxtDest.Text = _cfg.Dest;
        RbMirror.IsChecked = _cfg.Mode == "Mirror";
        RbAdditive.IsChecked = _cfg.Mode != "Mirror";
        _presetBoxes = new[]
        {
            (ChkExTemp, "temp"), (ChkExSystem, "system"),
            (ChkExDev, "dev"), (ChkExCache, "cache"),
        };
        foreach (var (box, id) in _presetBoxes)
            box.IsChecked = _cfg.ExcludePresets.Contains(id);
        ChkVersioned.IsChecked = _cfg.Versioned;
        NumVersionsKeep.Value = _cfg.VersionsToKeep;
        UpdateVersionedEnabledState();
        ChkSchedule.IsChecked = _cfg.ScheduleEnabled;
        ChkOnConnect.IsChecked = _cfg.TriggerOnConnect;
        _dayBoxes = new[]
        {
            (ChkMon, DayOfWeek.Monday), (ChkTue, DayOfWeek.Tuesday),
            (ChkWed, DayOfWeek.Wednesday), (ChkThu, DayOfWeek.Thursday),
            (ChkFri, DayOfWeek.Friday), (ChkSat, DayOfWeek.Saturday),
            (ChkSun, DayOfWeek.Sunday),
        };
        foreach (var (box, day) in _dayBoxes)
            box.IsChecked = _cfg.ScheduleDays.Contains(day);
        UpdateScheduleEnabledState();
        // Follow the system's 12- vs 24-hour clock preference (like the Mica
        // theme follows the OS), rather than pinning one in XAML. Clocks lists
        // the user's preferred identifiers; the first is the effective one.
        var clocks = Windows.System.UserProfile.GlobalizationPreferences.Clocks;
        if (clocks.Count > 0) TimeSchedule.ClockIdentifier = clocks[0];
        TimeSchedule.SelectedTime = ParseScheduleTime(_cfg.ScheduleTime);
        TxtAppDest.Text = _cfg.AppListDest;
        ChkExportSettings.IsChecked = _cfg.ExportAppSettings;

        // System Image page inputs.
        TxtImageTarget.Text = _cfg.ImageTarget;
        UpdateImageTargetKindLabel();
        ChkImageSchedule.IsChecked = _cfg.ImageScheduleEnabled;
        RbImageMonthly.IsChecked = _cfg.ImageCadence == "Monthly";
        RbImageDaily.IsChecked = _cfg.ImageCadence == "Daily";
        RbImageWeekly.IsChecked = _cfg.ImageCadence != "Monthly" && _cfg.ImageCadence != "Daily";
        int dayIdx = Array.IndexOf(_imageDayOrder, _cfg.ImageWeeklyDay);
        CmbImageWeeklyDay.SelectedIndex = dayIdx >= 0 ? dayIdx : 6;
        NumImageMonthlyDay.Value = _cfg.ImageMonthlyDay;
        if (clocks.Count > 0) TimeImage.ClockIdentifier = clocks[0];
        TimeImage.SelectedTime = ParseScheduleTime(_cfg.ImageScheduleTime);
        UpdateImageScheduleEnabledState();
        UpdateImageCadenceRows();
        _lastImageScheduleSig = ImageScheduleSignature(_cfg);

        // Track which folder row last held focus (for Remove). Handled at the
        // list level, not in the DataTemplate: a code-behind event handler
        // referenced from inside a DataTemplate is not reliably resolvable and
        // gets trimmed under NativeAOT.
        FolderList.GotFocus += OnFolderListGotFocus;

        // TabFocusNavigation="Once" re-enters each list at its first row. Send
        // focus back to the row that last held it when Tab arrives from outside.
        FolderList.GettingFocus += OnFolderListGettingFocus;
        AppListControl.GotFocus += OnAppListGotFocus;
        AppListControl.GettingFocus += OnAppListGettingFocus;

        // First-letter navigation: type a letter to jump to the matching row,
        // repeat to cycle. Matches the visible primary-column text (folder path,
        // app name).
        ListTypeAhead.Attach(FolderList, o => ((FolderPair)o).DisplaySource);
        ListTypeAhead.Attach(AppListControl, o => ((AppEntry)o).Name);

        WireFolderDirty();
        WireExcludeDirty();
        CheckScheduledTasksAtLaunch();

        // Initial population fired the dirty handlers; reset so the status
        // reflects the on-disk script.
        _dirty = false;
        _imageDirty = false;
        // Seed both pages' status text without announcing at launch. File Backup
        // is the active page, so refresh it last - its text is what the bar shows
        // and what _lastAnnouncedStatus should match.
        RefreshImageStatus(announce: false);
        RefreshScriptStatus(announce: false);

        // Before the first-ever run, the size-vs-space figures are the useful
        // launch info, so surface them like a save does. Once backups have
        // run, the status line carries the last run's health instead (see
        // RefreshScriptStatus) and the figures only refresh on a manual save.
        // Silent and off-thread (announce:false), so it never speaks over the
        // opening window or blocks it.
        if (File.Exists(GuardPaths.ScriptPath) && !_dirty
            && BackupHealth.ReadLog(GuardPaths.LogPath) is null)
            StartSpaceStatusCheck(announce: false);

        // Settings page (guard-prefs.ini): seed its controls and apply the theme.
        InitializeSettingsPage();

        AppWindow.Closing += OnAppWindowClosing;
        // A staged update (Install and Relaunch, or the install-on-exit mode) is
        // applied by a helper script that waits for this process to end; launch
        // it at the last moment so it never races a close the user cancels.
        Closed += OnWindowClosed;

        // The startup-page switch and the daily update check run after the
        // constructor: selecting a nav page kicks that page's lazy work, and
        // nothing reachable from here may touch the not-yet-live visual tree
        // (the XamlRoot fail-fast; see UpdateSaveEnabled's guard).
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyStartupPage();
            // Land keyboard focus on the selected page item in the nav, not the
            // bare pane: NVDA otherwise announces only "GUARD, pane" and the
            // user must Tab once before the nav is reachable.
            (Nav.SelectedItem as Control)?.Focus(FocusState.Programmatic);
            _ = AutoUpdateCheckAsync();
        });
    }

    // Ctrl+1..4 jump straight to a page from anywhere in the window (the nav is
    // otherwise several Tabs away). Wired as accelerators on the root Grid, so
    // they fire regardless of where focus sits. Setting SelectedItem runs the
    // normal page-switch path; focusing the item makes a screen reader follow.
    private void OnPageAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        object? item = sender.Key switch
        {
            Windows.System.VirtualKey.Number1 => NavFile,
            Windows.System.VirtualKey.Number2 => NavImage,
            Windows.System.VirtualKey.Number3 => NavApps,
            Windows.System.VirtualKey.Number4 => Nav.SettingsItem,
            _ => null,
        };
        if (item is null) return;
        Nav.SelectedItem = item;
        (item as Control)?.Focus(FocusState.Programmatic);
    }

    // =====================================================================
    //  PAGE SWITCH (lazy app scan)
    // =====================================================================
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // FileBackupPage / AppMgmtPage are created by InitializeComponent; this
        // can fire during it (NavFile.IsSelected="True"), before the rest of the
        // constructor runs, so guard on the pages existing.
        if (FileBackupPage == null || AppMgmtPage == null || SystemImagePage == null
            || SettingsPage == null) return;
        // The built-in Settings footer item has no Tag; it flags itself here.
        if (args.IsSettingsSelected)
        {
            _activePage = 3;
            FileBackupPage.Visibility = Visibility.Collapsed;
            SystemImagePage.Visibility = Visibility.Collapsed;
            AppMgmtPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            // Probe for winget once, lazily, like the app scan and the wbadmin
            // check: the card only appears when winget is actually missing.
            ProbeWingetForSettings();
            UpdateStatusBar();
            return;
        }
        SettingsPage.Visibility = Visibility.Collapsed;
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "file";
        if (tag == "apps")
        {
            _activePage = 1;
            FileBackupPage.Visibility = Visibility.Collapsed;
            SystemImagePage.Visibility = Visibility.Collapsed;
            AppMgmtPage.Visibility = Visibility.Visible;
            // announceStart:false - the nav is already announcing the newly
            // selected page; announcing the scan start at the same instant makes
            // a screen reader read them jumbled. The scan-complete summary is the
            // spoken cue instead.
            if (!_appScanned) { _appScanned = true; ScanApps(announceStart: false); }
        }
        else if (tag == "image")
        {
            _activePage = 2;
            FileBackupPage.Visibility = Visibility.Collapsed;
            AppMgmtPage.Visibility = Visibility.Collapsed;
            SystemImagePage.Visibility = Visibility.Visible;
            // Probe for wbadmin once, lazily, like the app scan: absent on Home,
            // where imaging self-disables. Repaint the status silently afterwards.
            if (!_imageChecked) { _imageChecked = true; CheckImageAvailability(); }
            RefreshImageStatus(announce: false);
        }
        else
        {
            _activePage = 0;
            AppMgmtPage.Visibility = Visibility.Collapsed;
            SystemImagePage.Visibility = Visibility.Collapsed;
            FileBackupPage.Visibility = Visibility.Visible;
        }
        // Repaint the status bar for the new page silently; switching pages is
        // not a status change worth a live-region announcement.
        UpdateStatusBar();
    }

    // Help and About are invoke-only footer items (SelectsOnInvoked="False"), so
    // they raise ItemInvoked without changing the selected page; dispatch them to
    // the existing handlers. Page items also raise this, but their work is done
    // by OnNavSelectionChanged, so they fall through.
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string)
        {
            case "help": OnHelp(NavHelp, new RoutedEventArgs()); break;
            case "about": OnAbout(NavAbout, new RoutedEventArgs()); break;
        }
    }

    // =====================================================================
    //  DIRTY TRACKING + STATUS
    // =====================================================================
    private void OnDirtyChanged(object sender, TextChangedEventArgs e) { _dirty = true; RefreshScriptStatus(); }
    private void OnDirtyChecked(object sender, RoutedEventArgs e) { _dirty = true; RefreshScriptStatus(); }

    // The schedule on/off box also greys out the day/time controls so it is clear
    // they only apply when a scheduled backup is enabled.
    private void OnScheduleEnabledChanged(object sender, RoutedEventArgs e)
    {
        _dirty = true;
        UpdateScheduleEnabledState();
        RefreshScriptStatus();
    }

    private void UpdateScheduleEnabledState()
    {
        // StackPanel has no IsEnabled (it is a Panel, not a Control), so grey out
        // the interactive leaves directly.
        bool on = ChkSchedule.IsChecked == true;
        foreach (var (box, _) in _dayBoxes) box.IsEnabled = on;
        if (TimeSchedule != null) TimeSchedule.IsEnabled = on;
    }
    private void OnScheduleTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) { _dirty = true; RefreshScriptStatus(); }

    // Like the schedule section, the versioning on/off box greys out its keep
    // count so it is clear the count only applies when versioning is enabled.
    private void OnVersionedChanged(object sender, RoutedEventArgs e)
    {
        _dirty = true;
        UpdateVersionedEnabledState();
        RefreshScriptStatus();
    }

    private void UpdateVersionedEnabledState()
    {
        if (NumVersionsKeep != null) NumVersionsKeep.IsEnabled = ChkVersioned.IsChecked == true;
    }

    private void OnVersionsKeepChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { _dirty = true; RefreshScriptStatus(); }

    private void WireFolderDirty()
    {
        _cfg.Folders.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (FolderPair f in e.NewItems) f.PropertyChanged += OnFolderItemChanged;
            _dirty = true; RefreshScriptStatus();
        };
        foreach (var f in _cfg.Folders) f.PropertyChanged += OnFolderItemChanged;
    }

    private void OnFolderItemChanged(object? s, PropertyChangedEventArgs e) { _dirty = true; RefreshScriptStatus(); }

    private void WireExcludeDirty()
    {
        _cfg.Excludes.CollectionChanged += (_, _) => { _dirty = true; RefreshScriptStatus(); };
    }

    private void OnFolderListGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FolderPair f)
        {
            _currentFolder = f;
            _lastFolderFocus = fe;
        }
    }

    private void OnAppListGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe) _lastAppFocus = fe;
    }

    private void OnFolderListGettingFocus(UIElement sender, GettingFocusEventArgs args)
        => RestoreListFocus(FolderList, _lastFolderFocus, args);

    private void OnAppListGettingFocus(UIElement sender, GettingFocusEventArgs args)
        => RestoreListFocus(AppListControl, _lastAppFocus, args);

    // When keyboard focus enters a list from outside, redirect it to the row that
    // last held focus instead of the first row. A remembered element whose XamlRoot
    // is null was detached (its row was removed or filtered out) - ignore it and let
    // the default first-row behaviour stand.
    private static void RestoreListFocus(ItemsControl list, FrameworkElement? remembered, GettingFocusEventArgs args)
    {
        if (args.InputDevice != FocusInputDeviceKind.Keyboard) return;
        if (remembered is null || remembered.XamlRoot is null) return;
        if (args.OldFocusedElement is DependencyObject old && IsDescendant(list, old)) return; // moving within the list
        if (args.NewFocusedElement is DependencyObject nw && !IsDescendant(list, nw)) return;  // not actually entering it
        if (args.TrySetNewFocusedElement(remembered)) args.Handled = true;
    }

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject? node)
    {
        for (var d = node; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    // Status-dot colours: green = saved and healthy, amber = needs attention
    // (unsaved changes, nothing saved yet, or low destination space).
    private static readonly Color StatusGreen = Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50);
    private static readonly Color StatusAmber = Color.FromArgb(0xFF, 0xD2, 0x99, 0x22);

    // Shared tail for every File Backup / System Image status update: repaint
    // the bar, announce only while that page owns it AND the text actually
    // changed (or toggling a checkbox would re-read the status on top of the
    // box's own state), and record the last-announced text page-scoped - a
    // silent repaint from an inactive page must not clobber the marker, or the
    // active page's next identical status would be re-announced (or a new one
    // suppressed).
    private void CommitPageStatus(int page, bool announce)
    {
        UpdateStatusBar();
        string text = page == 2 ? _imageStatusText : _fileStatusText;
        if (announce && _activePage == page && text != _lastAnnouncedStatus)
            Announce(StatusBarText);
        if (_activePage == page) _lastAnnouncedStatus = text;
    }

    // Shared run-busy focus discipline (File Backup, System Image, and the App
    // page's reinstall/update jobs): enable Stop and hand it focus BEFORE the
    // launchers grey out, and put focus back on the launcher before Stop greys
    // out. Disabling a focused button lets WinUI throw focus at an arbitrary
    // neighbour, and a screen reader cancels what it is speaking on a focus
    // event - which ate the end-of-run summary. Returns the launcher to
    // restore, or null when focus was elsewhere (then it is left alone).
    private Control? BeginRunBusy(Button stop, params Control[] launcherCandidates)
    {
        stop.IsEnabled = true;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
        Control? launcher = focused != null && Array.IndexOf(launcherCandidates, focused) >= 0
            ? focused : null;
        if (launcher != null) stop.Focus(FocusState.Programmatic);
        return launcher;
    }

    // Call AFTER the launchers are re-enabled, so the focus restore lands on a
    // live control.
    private void EndRunBusy(Button stop, Control? launcher)
    {
        if (launcher != null &&
            ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), stop))
            launcher.Focus(FocusState.Programmatic);
        stop.IsEnabled = false;
    }

    private void RefreshScriptStatus(bool announce = true)
    {
        if (StatusBarText == null) return;
        // This rewrite supersedes any in-flight space check: the check (capped
        // at two minutes) captures the line it started from and appends its
        // figures to THAT text, so without this a run finishing mid-check had
        // its fresh health line replaced by the stale pre-run text. The
        // check's own seq test drops its result once this bumps.
        _spaceCheckSeq++;
        // Status texts stay TERSE throughout: the bar is one line, so a
        // sentence too many visually truncates the part that matters (and pads
        // every screen-reader announcement). Detail belongs in the manual.
        if (!File.Exists(GuardPaths.ScriptPath))
        {
            _fileStatusBrush = new SolidColorBrush(StatusAmber);
            _fileStatusText = "No backup settings saved yet - click Save Settings first.";
        }
        else if (_dirty)
        {
            _fileStatusBrush = new SolidColorBrush(StatusAmber);
            _fileStatusText = "Unsaved changes - click Save Settings to apply them.";
        }
        else
        {
            // Saved and clean (the resting state, shown at launch, page
            // revisits and run ends; a just-pressed Save shows its own
            // explicit confirmation instead - see SaveAllAsync): the useful
            // fact here is not that settings are saved but whether backups
            // are actually happening.
            var now = DateTime.Now;
            var last = BackupHealth.ReadLog(GuardPaths.LogPath);
            if (last is null)
            {
                _fileStatusBrush = new SolidColorBrush(StatusGreen);
                _fileStatusText = "Backup settings saved. No backup has run yet.";
            }
            else
            {
                string when = BackupHealth.FriendlyWhen(last.When, now);
                var expected = _cfg.ScheduleEnabled
                    ? BackupHealth.PreviousScheduledRun(_cfg.ScheduleDays, _cfg.ScheduleTime, now)
                    : null;
                bool amber = true;
                string text;
                if (last.Outcome == RunOutcome.Errors)
                    text = "Last backup had errors (" + when + ") - open the last log.";
                else if (last.Outcome == RunOutcome.DidNotComplete)
                    text = "Last backup did not complete (" + when + ") - open the last log.";
                else if (BackupHealth.IsOverdue(last, expected, now))
                    text = "Backup overdue - last succeeded " + when + ".";
                else if (_cfg.TriggerOnConnect && !_cfg.ScheduleEnabled
                         && now - last.When > BackupHealth.OnConnectStale)
                    text = "Last backup was over a week ago (" + when + ") - connect your backup drive.";
                else
                {
                    amber = false;
                    text = "Last backup succeeded " + when + ".";
                }
                _fileStatusBrush = new SolidColorBrush(amber ? StatusAmber : StatusGreen);
                _fileStatusText = text;
            }
        }
        UpdateSaveEnabled();
        CommitPageStatus(0, announce);
    }

    // Save Settings is redundant once the on-disk script already matches the
    // saved config (nothing edited since the last save): a no-op save would just
    // rewrite identical files and re-announce "saved", so disable it then. It
    // stays enabled while there are unsaved edits or nothing has been saved yet.
    // A running backup owns the button (SetFileBusy) and is left untouched. If
    // the button is about to disable while it holds focus (the instant after a
    // save), hand focus to Run Now so a keyboard/screen-reader user is not
    // stranded on a control that just went unavailable.
    private void UpdateSaveEnabled()
    {
        if (BtnSave == null || _backupRunning) return;
        bool enable = _dirty || !File.Exists(GuardPaths.ScriptPath);
        // The focus rescue only matters after the window is up; this also runs
        // during construction (seeded status), when Content.XamlRoot is still
        // null and passing it to FocusManager would fail-fast across the WinRT
        // ABI. Guard on the root existing before querying focus.
        if (!enable && BtnSave.IsEnabled && Content?.XamlRoot is not null &&
            ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), BtnSave))
            BtnRunNow.Focus(FocusState.Programmatic);
        BtnSave.IsEnabled = enable;
    }

    // The status bar shows the focused page's status text (left) and that page's job
    // progress (right); both repaint here on a page switch, so the bar always
    // reflects the page you are on. A job on an unfocused page stays visible via its
    // nav ring, not the bar.
    private void UpdateStatusBar()
    {
        if (StatusBarText == null) return;
        if (_activePage == 0)
        {
            StatusDot.Visibility = Visibility.Visible;
            if (_fileStatusBrush != null) StatusDot.Fill = _fileStatusBrush;
            StatusBarText.Text = _fileStatusText;
        }
        else if (_activePage == 2)
        {
            // Same saved/unsaved dot semantic as File Backup.
            StatusDot.Visibility = Visibility.Visible;
            if (_imageStatusBrush != null) StatusDot.Fill = _imageStatusBrush;
            StatusBarText.Text = _imageStatusText;
        }
        else if (_activePage == 3)
        {
            // Settings persist as they change, so there is no saved/unsaved
            // state for the dot to reflect.
            StatusDot.Visibility = Visibility.Collapsed;
            StatusBarText.Text = "Settings are saved as soon as you change them.";
        }
        else
        {
            // The dot's saved/unsaved colour semantic does not apply to the
            // inventory summary; hide it rather than show a meaningless colour.
            StatusDot.Visibility = Visibility.Collapsed;
            StatusBarText.Text = _appStatusText;
        }
        // Repaint the right-hand progress from the focused page's snapshot.
        RenderStatusBar(_pageProg[_activePage]);
        // The bar is one line, so a long status visually truncates; mirror the
        // full text into a tooltip for mouse users (screen readers get the
        // full text from the element regardless).
        ToolTipService.SetToolTip(StatusBarText, StatusBarText.Text);
    }

    // Inventory status lives in the status bar (its single home); announce
    // only while App Management is the active page, since the bar shows the
    // file status otherwise (the text repaints on switching back regardless).
    private void AnnounceAppStatus()
    {
        UpdateStatusBar();
        if (_activePage == 1) Announce(StatusBarText);
    }

    // One-shot spoken messages (job start, end-of-run summary, cancellations) use
    // a UIA notification, not a live region: the notification carries its text in
    // the event, so the screen reader speaks exactly that with no dependency on
    // when an element's UIA Name catches up (live-region events on a just-updated
    // TextBlock can be dropped or read stale). Raised on the status bar text,
    // which is always present and visible: the per-page progress labels now live
    // inside collapsed "Output details" expanders, and a collapsed element is
    // outside the UIA tree, so a notification on one is silently dropped (this is
    // why preview/backup completion went unspoken).
    private void AnnounceNotification(string text)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(StatusBarText)
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusBarText);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                text, "GuardJobDone");
        }
        catch { }
    }

    // AnnounceNotification after a settle delay. A job's start and end move
    // keyboard focus (to/from the Stop button), and a screen reader cancels what
    // it's speaking on a focus event - so a notification raised too close to the
    // focus change gets cut off (short messages survived, long summaries read as
    // silence). The delay lets the focus announcement clear first. Start works at
    // 800ms because script startup already adds ~1s; end fires straight after the
    // focus restore and needs the full two seconds.
    private async void AnnounceSettled(string text, int delayMs = 800)
    {
        await System.Threading.Tasks.Task.Delay(delayMs);
        DispatcherQueue.TryEnqueue(() => AnnounceNotification(text));
    }

    private static void Announce(UIElement el)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(el)
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(el);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch { }
    }

    // Shared by every CheckBox and RadioButton that carries an AccessKey (both
    // are ToggleButtons). Confirmed live with NVDA: Tab+Space announces the new
    // state, but WinUI's default Alt access-key handling toggles the control
    // without moving keyboard focus there first, so NVDA has nothing focused to
    // read. Move focus explicitly so the access key gets the same
    // focus-then-toggle sequence Tab+Space already gets right; the default
    // toggle still runs afterwards (Handled is left false), so each control's
    // own Checked/Unchecked handler still fires.
    private void OnToggleAccessKeyInvoked(UIElement sender, AccessKeyInvokedEventArgs args)
    {
        if (sender is Control control) control.Focus(FocusState.Keyboard);
    }

    // Per-page progress model. The status bar's right-hand area shows only the
    // FOCUSED page's job (repainted on a page switch, like the left-hand status), and
    // each page's nav ring shows that page's own job - a filling determinate arc once
    // a real percentage is known, spinning while a phase has none. Index by the
    // _activePage convention: 0 File Backup, 1 App Management, 2 System Image. The nav
    // ring is what keeps a job on an unfocused page discoverable; the bar no longer
    // mirrors it, so the two surfaces don't duplicate each other. Index 3 is the
    // Settings page: it never runs a job, but UpdateStatusBar reads the focused
    // page's snapshot unconditionally, so it needs a (permanently empty) slot.
    private sealed class PageProgress
    {
        public bool Running;               // a job is live on this page (drives the ring)
        public bool Indeterminate = true;  // phase has no measurable percent -> spin
        public double Max = 1;
        public double Value;
        public string Text = "";           // current progress line, or the lingering outcome
        public bool AreaVisible;           // status-bar right area shown (running or outcome)
        public bool BarVisible;            // the moving bar shown (vs outcome text only)
    }
    private readonly PageProgress[] _pageProg = { new(), new(), new(), new() };

    private ProgressRing NavRingFor(int page) => page == 1 ? NavAppsRing : page == 2 ? NavImageRing : NavFileRing;
    private NavigationViewItem NavItemFor(int page) => page == 1 ? NavApps : page == 2 ? NavImage : NavFile;
    private int PageOfBar(ProgressBar bar) => bar == AppProgress ? 1 : bar == ImageProgress ? 2 : 0;

    // Mutate one page's snapshot on the UI thread, then render it. Callers may be on
    // a background thread (script output, worker tasks), so the marshalling lives
    // here rather than in each call site.
    private void Progress(int page, Action<PageProgress> mutate)
        => DispatcherQueue.TryEnqueue(() => { mutate(_pageProg[page]); ApplyPageProgress(page); });

    // Render a page's snapshot to its nav ring (always) and, when it is the focused
    // page, to the shared status-bar right area. UI thread only.
    private void ApplyPageProgress(int page)
    {
        var p = _pageProg[page];
        var ring = NavRingFor(page);
        ring.Visibility = p.Running ? Visibility.Visible : Visibility.Collapsed;
        ring.IsActive = p.Running;
        ring.IsIndeterminate = p.Indeterminate;
        if (!p.Indeterminate)
        {
            if (p.Max > 0) ring.Maximum = p.Max;
            ring.Value = p.Value;
        }
        // "running" rides on HelpText (read after the page name on focus) only while
        // a job is live; cleared when done. Just "running", not "<page> running", or
        // a screen reader would read the page name twice.
        AutomationProperties.SetHelpText(NavItemFor(page), p.Running ? "running" : "");
        if (page == _activePage) RenderStatusBar(p);
    }

    // Paint the shared status-bar progress controls from a snapshot. Called by
    // ApplyPageProgress for the focused page and by UpdateStatusBar on a page switch.
    private void RenderStatusBar(PageProgress p)
    {
        StatusBarProgressArea.Visibility = p.AreaVisible ? Visibility.Visible : Visibility.Collapsed;
        StatusBarProgress.Visibility = (p.AreaVisible && p.BarVisible) ? Visibility.Visible : Visibility.Collapsed;
        StatusBarProgress.IsIndeterminate = p.Indeterminate;
        if (p.Max > 0) StatusBarProgress.Maximum = p.Max;
        StatusBarProgress.Value = p.Value;
        StatusBarProgressText.Text = p.Text;
        // Same truncation safety net as the main status text.
        ToolTipService.SetToolTip(StatusBarProgressText, p.Text);
    }

    // Advances only the backup bar's value (in-page bar + the page's snapshot),
    // leaving the max and the "Backing up: ... (n of N)" label untouched; used for
    // the frequent within-folder byte updates so they neither rewrite the label nor
    // reset the max. Visibility is owned by ShowStatusBarProgress, not touched here.
    private void SetFileProgressValue(double val)
    {
        DispatcherQueue.TryEnqueue(() => FileProgress.Value = val);
        Progress(0, p => p.Value = val);
    }

    // Determinate update for a page's bar: the in-page bar/label plus the page's
    // snapshot (which feeds the nav ring and, when focused, the status bar).
    // Visibility stays with ShowStatusBarProgress so a determinate outcome update
    // after a run cannot re-show a bar that was just hidden.
    private void SetProgress(ProgressBar bar, TextBlock lbl, double max, double val, string text)
    {
        if (bar == null) return;
        int page = PageOfBar(bar);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (max > 0) bar.Maximum = max;
            bar.Value = val;
            if (lbl != null) lbl.Text = text;
        });
        Progress(page, p =>
        {
            p.Indeterminate = false;
            if (max > 0) p.Max = max;
            p.Value = val;
            p.Text = text;
        });
    }

    // The status-bar progress area shows live progress while a page's job runs; when
    // it ends only the bar hides, and the area keeps the final outcome (summary /
    // cancelled / done counts) until that page's next job, so the read-status-bar
    // hotkey reports how the last run ended while the page is focused. The area fully
    // collapses only when there's no outcome to show (before any job, or after a
    // launch failure).
    private void ShowStatusBarProgress(int page, bool show)
    {
        Progress(page, p =>
        {
            if (show) { p.AreaVisible = true; p.BarVisible = true; }
            else { p.BarVisible = false; p.AreaVisible = !string.IsNullOrEmpty(p.Text); }
        });
    }

    private void AppendOut(TextBox box, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            box.Text += text;
            ScrollToEnd(box);
        });
    }

    // Keep the output console scrolled to the newest line. Select(end, 0) only
    // scrolls a WinUI 3 TextBox while focused, so drive the template's ScrollViewer
    // directly. ChangeView (animation off) moves the viewport without taking focus
    // or raising any focus/live-region event, so a screen reader's reading position
    // isn't disturbed beyond the text change. UpdateLayout first so ScrollableHeight
    // reflects the just-appended line.
    private static void ScrollToEnd(TextBox box)
    {
        box.UpdateLayout();
        if (FindScrollViewer(box) is ScrollViewer sv)
            sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is ScrollViewer nested) return nested;
        }
        return null;
    }

    // =====================================================================
    //  BROWSE / TEST / OPEN
    // =====================================================================
    private async void OnBrowseDest(object sender, RoutedEventArgs e) => await BrowseInto(TxtDest);
    private async void OnBrowseApp(object sender, RoutedEventArgs e) => await BrowseInto(TxtAppDest);
    private async void OnTestDest(object sender, RoutedEventArgs e) => await TestConnection(TxtDest.Text);
    private async void OnTestApp(object sender, RoutedEventArgs e) => await TestConnection(TxtAppDest.Text);
    private void OnOpenLog(object sender, RoutedEventArgs e) => OpenPath(GuardPaths.LogPath, "No log found yet. Run a backup first.");
    private void OnOpenDest(object sender, RoutedEventArgs e) => OpenPath(TxtDest.Text);
    private void OnOpenAppDest(object sender, RoutedEventArgs e) => OpenPath(TxtAppDest.Text);

    private async System.Threading.Tasks.Task BrowseInto(TextBox box)
    {
        Windows.Storage.StorageFolder? folder;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeFilter.Add("*");
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            // The WinRT folder picker can throw in an unpackaged app; failing
            // the browse (not the whole app) matches TestConnection's handling
            // of its own I/O just below.
            await ShowMessageAsync("GUARD", "Could not open the folder picker:\n\n" + ex.Message);
            return;
        }
        if (folder != null) box.Text = folder.Path;
    }

    private async System.Threading.Tasks.Task TestConnection(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) { await ShowMessageAsync("GUARD", "Enter a destination path first."); return; }
        try
        {
            if (Directory.Exists(path)) { await ShowMessageAsync("GUARD", "Reachable:\n" + path); return; }
            // Creating the folder here is how a typed-but-new destination gets
            // materialized (neither Save nor the script creates it; the script
            // aborts when it is missing) - but a "test" must not write silently,
            // so ask first.
            if (!await ShowConfirmAsync("GUARD",
                "Not found:\n" + path + "\n\nCreate this folder now so it can be used as a destination?"))
                return;
            Directory.CreateDirectory(path);
            await ShowMessageAsync("GUARD", "Created and reachable:\n" + path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "Not reachable:\n" + path + "\n\n" + ex.Message);
        }
    }

    // notFound overrides the missing-path message; callers with an internal path
    // (the logs) pass a plain-language line, since echoing Logs\backup_last.log
    // means nothing to the user. Folder opens keep the default, which shows the
    // path the user typed.
    private async void OpenPath(string? path, string? notFound = null)
    {
        try
        {
            path = (path ?? "").Trim();
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                await ShowMessageAsync("GUARD", notFound ?? ("Not found:\n" + path));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "Could not open:\n" + path + "\n\n" + ex.Message);
        }
    }

    private async void OnHelp(object sender, RoutedEventArgs e)
    {
        if (File.Exists(GuardPaths.ManualPath)) { OpenPath(GuardPaths.ManualPath); return; }
        try { Process.Start(new ProcessStartInfo(GuardPaths.RepoUrl) { UseShellExecute = true }); }
        catch (Exception ex) { await ShowMessageAsync("GUARD", "Could not open help:\n\n" + ex.Message); }
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.AboutDialog { XamlRoot = Content.XamlRoot };
        await ShowDialogAsync(dlg);
        // Runs after About closes: only one ContentDialog may be open at a time,
        // and the check's result is itself a dialog.
        if (dlg.CheckUpdatesRequested) await CheckForUpdatesNowAsync();
    }

    // =====================================================================
    //  DIALOG HELPERS
    // =====================================================================
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // ---- Window icon ----------------------------------------------------
    // The exe's embedded icon group (csproj ApplicationIcon) doubles as the
    // window icon, so no loose .ico ships next to GUARD.exe. Both WM_SETICON
    // slots are set: ICON_SMALL feeds the title bar at 16 px, ICON_BIG feeds
    // Alt-Tab at 32 px; a single AppWindow.SetIcon HICON would leave Windows
    // to rescale whichever slot it lacks. The handles stay in use for the
    // window's lifetime, so they are never destroyed.
    private const uint WM_SETICON = 0x0080;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out nint largeIcon, out nint smallIcon, uint count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    private void SetWindowIcon()
    {
        if (Environment.ProcessPath is not { } exe) return;
        ExtractIconExW(exe, 0, out nint big, out nint small, 1);
        if (small != 0) SendMessageW(WindowHandle, WM_SETICON, 0, small); // ICON_SMALL
        if (big != 0) SendMessageW(WindowHandle, WM_SETICON, 1, big);     // ICON_BIG
    }

    // ---- Minimum window size ------------------------------------------------
    // WinUI 3 exposes no minimum-size property, so the resize floor is enforced
    // the classic way: a window subclass that answers WM_GETMINMAXINFO with a
    // minimum track size. The floor is kept in DIPs and converted to physical
    // pixels per the window's current DPI, so it scales with the display. The
    // width is chosen so the bottom action bar and the App Management toolbar
    // (filter box + count) never clip on the right.
    private const int MinWidthDip = 860;
    private const int MinHeightDip = 620;
    private const uint WM_GETMINMAXINFO = 0x0024;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    private unsafe void EnableMinimumWindowSize()
    {
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> proc = &MinSizeSubclassProc;
        SetWindowSubclass(WindowHandle, (nint)proc, 1, 0);
    }

    // Static + UnmanagedCallersOnly (not an instance delegate) so the callback
    // is a plain function pointer that survives NativeAOT without a kept-alive
    // delegate. The single main window needs no per-instance state here.
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static unsafe nint MinSizeSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO && lParam != 0)
        {
            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;
            MINMAXINFO* mmi = (MINMAXINFO*)lParam;
            mmi->ptMinTrackSize.X = (int)(MinWidthDip * scale);
            mmi->ptMinTrackSize.Y = (int)(MinHeightDip * scale);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // AppWindow.Resize takes physical pixels; size in DIPs so the window is the
    // same effective width on any display scaling (otherwise a 150% display
    // shrinks the usable area below the content width and clips controls).
    private void SizeToDips(int dipWidth, int dipHeight)
    {
        try
        {
            uint dpi = GetDpiForWindow(WindowHandle);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;
            int w = (int)(dipWidth * scale);
            int h = (int)(dipHeight * scale);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            CenterInWorkArea(w, h);
        }
        catch { }
    }

    // Centre the window in the display work area after resizing. The OS default
    // cascade position can open a tall window with its title bar partway down
    // the screen, running the bottom off the display; clamping x/y to at least
    // the work-area origin keeps the title bar at the top when the window is
    // taller than the work area, and otherwise centres it.
    private void CenterInWorkArea(int width, int height)
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (area is null) return;
        var work = area.WorkArea;
        int x = work.X + Math.Max(0, (work.Width - width) / 2);
        int y = work.Y + Math.Max(0, (work.Height - height) / 2);
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    // Single funnel for every ContentDialog.ShowAsync so two can never overlap.
    // Returns None if a dialog is already open (e.g. an access key fired behind a
    // modal dialog), which callers treat as "no/cancel".
    private async System.Threading.Tasks.Task<ContentDialogResult> ShowDialogAsync(ContentDialog dlg)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        // Dialogs render in the popup layer, which does not pick up the root
        // element's RequestedTheme override; mirror it so a pinned Light/Dark
        // theme (Settings page) applies to every dialog too.
        if (Content is FrameworkElement root) dlg.RequestedTheme = root.RequestedTheme;
        _dialogOpen = true;
        try { return await dlg.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string content)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "OK"
        };
        await ShowDialogAsync(dlg);
    }

    private async System.Threading.Tasks.Task<bool> ShowConfirmAsync(string title, string content, string yes = "Yes", string no = "No")
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = yes,
            CloseButtonText = no,
            DefaultButton = ContentDialogButton.Primary
        };
        return await ShowDialogAsync(dlg) == ContentDialogResult.Primary;
    }

    // Notepad-style three-way prompt for unsaved File Backup settings on close.
    // Primary = Save (the default, matching Win32 convention), Secondary = Don't
    // Save, Close = Cancel; the caller maps each result to save/discard/stay.
    // The S / N / C mnemonics match Notepad's; they can safely reuse letters the
    // pages behind use, since a modal dialog is its own access-key scope.
    private async System.Threading.Tasks.Task<ContentDialogResult> ShowSaveOnCloseAsync()
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "GUARD",
            Content = "You have unsaved changes.\n\nDo you want to save them before closing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            // The secondary/close mnemonics ride in a Style; the primary is the
            // default button, whose Style the dialog overwrites, so its key is
            // set on the realized button below (see UiHelpers.AccessKeyButtonStyle).
            SecondaryButtonStyle = Views.UiHelpers.AccessKeyButtonStyle("N"),
            CloseButtonStyle = Views.UiHelpers.AccessKeyButtonStyle("C"),
        };
        dlg.Opened += (_, _) =>
        {
            if (Views.UiHelpers.FindDescendantByName(dlg, "PrimaryButton") is { } primary)
                primary.AccessKey = "S";
        };
        return await ShowDialogAsync(dlg);
    }

    // =====================================================================
    //  CLOSE GUARD
    // =====================================================================
    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        // The recovery-USB build runs inside its modal wizard, so the usual
        // close prompts cannot show over it (ShowDialogAsync refuses a second
        // dialog). Block the close outright and say so via a notification:
        // the elevated build cannot be killed from here, and closing would
        // strand it writing the USB with its progress surface (and the
        // "do not remove the drive" warning) gone. The wizard's own Cancel
        // is the way to stop it first.
        if (_recoveryDialog?.IsBuilding == true)
        {
            args.Cancel = true;
            AnnounceNotification("A recovery USB is being built. Finish or cancel it in the wizard before closing GUARD.");
            return;
        }

        // Two independent reasons to pause a close: unsaved File Backup settings,
        // and any background job still running (backup, image, reinstall, export,
        // image listing, app scan). If neither applies, let the close proceed
        // untouched; otherwise cancel it and drive the close ourselves after the
        // prompts (the second Close() re-enters this handler with _allowClose
        // set, so it sails straight through).
        bool busy = _backupRunning || _reinstalling || _imageRunning || _exporting || _imageListing || _scanning;
        if (!_dirty && !_imageDirty && !busy) return;
        args.Cancel = true;

        // Unsaved changes first (the common case): Save / Don't Save / Cancel.
        // Covers either page's unsaved edits; Save persists whichever are dirty.
        if (_dirty || _imageDirty)
        {
            var choice = await ShowSaveOnCloseAsync();
            if (choice == ContentDialogResult.None) return;     // Cancel: stay open
            // Save: a save that fails validation (e.g. an empty destination)
            // leaves the settings unsaved, so keep the window open to fix it.
            // Each SaveAsync surfaces the reason and clears its dirty flag.
            if (choice == ContentDialogResult.Primary)
            {
                if (_dirty && !await SaveAllAsync()) return;
                if (_imageDirty && !await SaveImageAsync()) return;
            }
            // Don't Save falls through and discards the edits.
        }

        // Recomputed, not the snapshot from above: the save prompt can sit open
        // while a job finishes, and a stale snapshot would then warn about a
        // job that is no longer running.
        busy = _backupRunning || _reinstalling || _imageRunning || _exporting || _imageListing || _scanning;
        if (busy)
        {
            // The elevated image cannot be cancelled from this un-elevated
            // process (only wbadmin stop job can), so be honest that it
            // continues; the backup/reinstall trees are killed below.
            string what = _imageRunning
                ? "A system image is still running. It runs with Administrator rights, so it will keep running in the background after GUARD closes."
                : _updateAllElevated
                ? "An app update is still running with Administrator rights, so it will keep running in the background after GUARD closes."
                : _reinstalling ? "An app reinstall is still running. Closing GUARD stops it."
                : _exporting ? "Copying app settings is still running. Closing GUARD now will leave an incomplete export."
                : _imageListing ? "Reading the list of existing system images is still running."
                : _scanning ? "Scanning installed apps is still running."
                : "A backup is still running. Closing GUARD stops it.";
            if (!await ShowConfirmAsync("GUARD", what + " Close anyway?")) return;
            // Cancel both jobs so no cmd/robocopy/winget tree outlives the
            // window; the kill registrations run synchronously inside Cancel.
            _runCts?.Cancel();
            _reinstallCts?.Cancel();
        }

        _allowClose = true;
        Close();
    }
}
