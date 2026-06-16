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

    // Cancellation for the two long-running jobs. The Stop buttons only ever
    // call Cancel(); the kill of the underlying process tree hangs off the
    // token (a Register callback), so cancel-vs-natural-exit races collapse to
    // a harmless Kill-after-exit that the registration swallows. Both fields
    // are created, cancelled and disposed on the UI thread only.
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reinstallCts;
    private bool _backupRunning;

    private bool _dirty;
    private int _progTotal;

    // Per-run robocopy summary accumulation; recreated by RunScript so a stale
    // parser from a previous run can never leak counts into the next one.
    private RobocopySummaryParser? _summaryParser;
    private bool _runIsPreview;

    // End-of-run summary held back until the run's focus churn settles; see
    // SetFileBusy for why announcing earlier loses the speech.
    private string? _runDoneAnnounce;

    // Save reentrancy guard (the save is now async and non-blocking, so the
    // button can be pressed again mid-save) and the staleness counter for the
    // background space/size status check.
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

    // Included sources that were unreachable at the last save. Advisory only:
    // the generated script SKIPs them at run time, so a save never blocks on
    // them. OnSave folds these into its dialog; RunScript prints them in the
    // output box instead so the run is not interrupted by a modal.
    private List<string> _missingSources = new();

    // The App Management scan/import summary; the status bar is its only home
    // (an in-place copy under the list was removed as redundant), so it lives
    // here for repainting the bar on tab switches.
    private string _appStatusText = "Open this tab to scan installed apps.";

    private bool _appScanned;
    private bool _scanning;
    private bool _reinstalling;
    private bool _exporting;
    private bool _wingetAvailable;
    private bool _allowClose;

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
        // 940 rather than the previous 900: the status bar row takes ~40 DIPs,
        // and the extra height keeps the tab content area unchanged.
        SizeToDips(820, 940);

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
        RefreshNextRun();

        // Initial population fired the dirty handlers; reset so the status
        // reflects the on-disk script.
        _dirty = false;
        // Seed the status text without announcing it at launch.
        RefreshScriptStatus(announce: false);

        AppWindow.Closing += OnAppWindowClosing;
    }

    // =====================================================================
    //  TAB SWITCH (lazy app scan)
    // =====================================================================
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // announceStart:false - on a tab switch the Pivot is already announcing
        // the newly selected tab; announcing the scan start at the same instant
        // makes a screen reader read them jumbled ("scanning... App Management
        // selected"). The scan-complete summary is the spoken cue instead.
        if (Tabs.SelectedIndex == 1 && !_appScanned) { _appScanned = true; ScanApps(announceStart: false); }
        // Repaint the status bar for the new tab silently; switching tabs is
        // not a status change worth a live-region announcement.
        UpdateStatusBar();
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

    // Status-dot colors: green = saved and healthy, amber = needs attention
    // (unsaved changes, nothing saved yet, or low destination space).
    private static readonly Color StatusGreen = Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50);
    private static readonly Color StatusAmber = Color.FromArgb(0xFF, 0xD2, 0x99, 0x22);

    private void RefreshScriptStatus(bool announce = true)
    {
        if (StatusBarText == null) return;
        if (!File.Exists(GuardPaths.ScriptPath))
        {
            _fileStatusBrush = new SolidColorBrush(StatusAmber);
            _fileStatusText = "No settings saved yet. Click Save Settings before running a backup.";
        }
        else if (_dirty)
        {
            _fileStatusBrush = new SolidColorBrush(StatusAmber);
            _fileStatusText = "You have unsaved changes. Click Save Settings to apply them.";
        }
        else
        {
            _fileStatusBrush = new SolidColorBrush(StatusGreen);
            _fileStatusText = "Settings saved. Last updated " +
                File.GetLastWriteTime(GuardPaths.ScriptPath).ToString("yyyy-MM-dd HH:mm") + ".";
        }
        UpdateStatusBar();
        // Only re-announce when the message actually changed; otherwise toggling
        // each day checkbox would re-read the status line on top of the box's own
        // checked/unchecked state. Announce only while the bar is showing this
        // text (File Backup active); the bar repaints silently on a tab switch.
        if (announce && Tabs.SelectedIndex == 0 && _fileStatusText != _lastAnnouncedStatus)
            Announce(StatusBarText);
        _lastAnnouncedStatus = _fileStatusText;
    }

    // The status bar shows the active tab's status text; a running job's
    // progress is mirrored into the bar's progress area independently (see
    // SetProgress), so a job stays visible from either tab.
    private void UpdateStatusBar()
    {
        if (StatusBarText == null || Tabs == null) return;
        if (Tabs.SelectedIndex == 0)
        {
            StatusDot.Visibility = Visibility.Visible;
            if (_fileStatusBrush != null) StatusDot.Fill = _fileStatusBrush;
            StatusBarText.Text = _fileStatusText;
        }
        else
        {
            // The dot's saved/unsaved colour semantic does not apply to the
            // inventory summary; hide it rather than show a meaningless colour.
            StatusDot.Visibility = Visibility.Collapsed;
            StatusBarText.Text = _appStatusText;
        }
    }

    // Inventory status lives in the status bar (its single home); announce
    // only while App Management is the active tab, since the bar shows the
    // file status otherwise (the text repaints on switching back regardless).
    private void AnnounceAppStatus()
    {
        UpdateStatusBar();
        if (Tabs.SelectedIndex == 1) Announce(StatusBarText);
    }

    // One-shot spoken messages (end-of-run summary, cancellations) use a UIA
    // notification instead of a live region: the notification carries its text
    // inside the event, so the screen reader speaks exactly that string with no
    // dependency on when the element's UIA Name catches up - live-region events
    // on a just-updated TextBlock can be dropped or read stale.
    private static void AnnounceNotification(UIElement el, string text)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(el)
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(el);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                text, "GuardJobDone");
        }
        catch { }
    }

    // AnnounceNotification after a settle delay. A job's start and end move
    // keyboard focus (to and from the Stop button), and a screen reader
    // cancels whatever it is speaking when it processes a focus event - so a
    // notification raised too close to the focus change gets cut off (short
    // messages survived, long summaries read as silence). The delay lets the
    // focus announcement clear first. The start announcement works at 800ms
    // because script startup already adds ~1s; the end announcement fires
    // straight after the focus restore and needs the full two seconds.
    private async void AnnounceSettled(UIElement el, string text, int delayMs = 800)
    {
        await System.Threading.Tasks.Task.Delay(delayMs);
        DispatcherQueue.TryEnqueue(() => AnnounceNotification(el, text));
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

    // =====================================================================
    //  SETTINGS HARVEST / SAVE
    // =====================================================================
    private void HarvestUi()
    {
        _cfg.Dest = (TxtDest.Text ?? "").Trim();
        _cfg.Mode = RbMirror.IsChecked == true ? "Mirror" : "Additive";
        // Custom exclusions already live in _cfg.Excludes via two-way binding;
        // only the preset checkboxes need harvesting.
        _cfg.ExcludePresets = new List<string>();
        foreach (var (box, id) in _presetBoxes)
            if (box.IsChecked == true) _cfg.ExcludePresets.Add(id);
        _cfg.Versioned = ChkVersioned.IsChecked == true;
        // NumberBox.Value is NaN while the field is cleared; fall back to the
        // last saved count rather than writing NaN into the keep count.
        if (!double.IsNaN(NumVersionsKeep.Value))
            _cfg.VersionsToKeep = Math.Clamp((int)NumVersionsKeep.Value, 1, 365);
        _cfg.ScheduleEnabled = ChkSchedule.IsChecked == true;
        _cfg.TriggerOnConnect = ChkOnConnect.IsChecked == true;
        _cfg.ScheduleDays = new List<DayOfWeek>();
        foreach (var (box, day) in _dayBoxes)
            if (box.IsChecked == true) _cfg.ScheduleDays.Add(day);
        _cfg.ScheduleTime = FormatScheduleTime(TimeSchedule.SelectedTime, _cfg.ScheduleTime);
        _cfg.AppListDest = (TxtAppDest.Text ?? "").Trim();
        _cfg.ExportAppSettings = ChkExportSettings.IsChecked == true;
    }

    // Returns false (after showing a message) when a required value is missing.
    private async System.Threading.Tasks.Task<bool> SaveAllAsync()
    {
        HarvestUi();
        if (string.IsNullOrEmpty(_cfg.Dest))
        {
            await ShowMessageAsync("GUARD", "Enter a backup destination first.\n\nType a folder path next to \"Backup destination\", or use the Browse button to pick one.");
            return false;
        }
        if (_cfg.ScheduleEnabled && _cfg.ScheduleDays.Count == 0)
        {
            await ShowMessageAsync("GUARD", "Pick at least one day for the scheduled backup, or turn the schedule off.");
            return false;
        }
        // The settings and script are written synchronously (fast, and the
        // ground truth the rest of the app reads), then everything slow runs
        // off the UI thread so the window never freezes: the scheduled-task
        // state is applied in one batched PowerShell invocation (each extra
        // powershell.exe start pays a multi-second module import - this used
        // to be 3-4 sequential ones and froze the UI for tens of seconds),
        // and a dead UNC source can make Directory.Exists block for seconds.
        if (_saving) return false;
        _saving = true;
        try
        {
            SettingsStore.Save(_cfg);
            BackupScript.Write(_cfg);
            _dirty = false;
            RefreshScriptStatus();
            // Save Settings is the single source of truth for both scheduled
            // tasks: each is registered when its own option is on and removed
            // when not, so the schedule and the on-connect trigger toggle
            // independently (ApplyAll handles both plus the legacy-name cleanup).
            var applied = await System.Threading.Tasks.Task.Run(
                () => ScheduledTasks.ApplyAll(_cfg));
            _taskError = applied.Error;
            LblNextRun.Text = applied.NextRun == null
                ? "Next run: (no scheduled task)" : "Next run: " + applied.NextRun;
            _missingSources = await System.Threading.Tasks.Task.Run(
                () => SaveValidation.UnreachableSources(_cfg.Folders));
            return true;
        }
        finally { _saving = false; }
    }

    // A successful save shows no dialog: the status line (a live region, so it
    // is announced) already reads "Settings saved..." and later gains the
    // space/size figures from the background check. Dialogs are reserved for
    // actual problems - a task-registration failure or unreachable sources.
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!await SaveAllAsync()) return;

        if (_taskError != null)
        {
            await ShowMessageAsync("GUARD", "Settings saved, but registering a scheduled task reported a problem:\n\n" + _taskError);
            return;
        }
        if (_missingSources.Count > 0)
            await ShowMessageAsync("GUARD", "Settings saved. Note: " + DescribeMissingSources(_missingSources)
                + "\n\nThey will be skipped if still unreachable when the backup runs.");

        StartSpaceStatusCheck();
    }

    // Background space/size check; appends its findings to the saved-status
    // line when done. Out of the modal path entirely, the size estimate can
    // afford a cap long enough to usually finish, so the figure is normally
    // the full total rather than a lower bound. The sequence counter plus the
    // dirty re-check drop a stale result if the user edited or saved again
    // while the walk was still running.
    private async void StartSpaceStatusCheck()
    {
        int seq = ++_spaceCheckSeq;

        // Interim placeholder so the line never sits silently mid-check; the
        // result replaces it (rebuilt from baseText, not appended) when done.
        string baseText = _fileStatusText;
        SetFileStatusText(baseText + " Calculating backup size and destination space...");

        var estimateTask = SaveValidation.EstimateBackupSizeAsync(_cfg.Folders, SaveValidation.EstimateCap);
        var freeTask = System.Threading.Tasks.Task.Run(() => SaveValidation.TryGetFreeSpace(_cfg.Dest));
        long? free = await freeTask;
        var est = await estimateTask;

        // A stale result (the user edited or saved again) leaves the line to
        // whoever rewrote it; RefreshScriptStatus has already replaced the text.
        if (seq != _spaceCheckSeq || _dirty) return;

        // Deliberately terse: this rides on the end of the status line, so a
        // sentence per fact would scroll the line off the window and pad the
        // screen-reader announcement. Tight space gets a Warning: prefix and
        // keeps the dot amber; the full explanation lives in the user manual.
        string extra;
        if (free is not long freeBytes)
        {
            extra = " Destination space could not be checked.";
        }
        else
        {
            bool tight = est.Bytes > 0 && est.Bytes > freeBytes * 0.9;
            extra = tight ? " Warning: space may be too low." : "";
            if (est.Bytes > 0)
                extra += " Backup size: " + (est.Complete ? "" : "at least ")
                    + SaveValidation.FormatBytes(est.Bytes) + ";";
            extra += " destination available space: " + SaveValidation.FormatBytes(freeBytes) + ".";
            if (tight) _fileStatusBrush = new SolidColorBrush(StatusAmber);
        }
        SetFileStatusText(baseText + extra);
    }

    // Mid-flow file-status updates (the space-check placeholder and result)
    // route through the status bar like RefreshScriptStatus does: repaint the
    // bar, announce only while File Backup is the active tab, and record the
    // text so an unchanged status is not re-spoken.
    private void SetFileStatusText(string text)
    {
        _fileStatusText = text;
        UpdateStatusBar();
        if (Tabs.SelectedIndex == 0 && _fileStatusText != _lastAnnouncedStatus)
            Announce(StatusBarText);
        _lastAnnouncedStatus = _fileStatusText;
    }

    private static string DescribeMissingSources(List<string> missing)
    {
        string list = "\n" + string.Join("\n", missing);
        return missing.Count == 1
            ? "this source folder is not currently reachable:" + list
            : "these source folders are not currently reachable:" + list;
    }

    // Off the UI thread: the query launches powershell.exe, whose cold start
    // pays a multi-second module import. The label keeps its "(unknown)"
    // placeholder until the answer arrives. Saves do not call this; ApplyAll
    // returns the next run from its own batched invocation.
    private async void RefreshNextRun()
    {
        if (LblNextRun == null) return;
        var next = await System.Threading.Tasks.Task.Run(
            () => ScheduledTasks.QueryNextRun(GuardPaths.FileTaskName));
        LblNextRun.Text = next == null ? "Next run: (no scheduled task)" : "Next run: " + next;
    }

    // Settings store the daily run time as an "HH:mm" string; the TimePicker
    // works in TimeSpan. These convert between the two, falling back to the
    // existing value if the picker has no selection.
    private static TimeSpan? ParseScheduleTime(string? text)
    {
        text = (text ?? "").Trim();
        if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t.TimeOfDay;
        if (DateTime.TryParseExact(text, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            return t.TimeOfDay;
        return null;
    }

    private static string FormatScheduleTime(TimeSpan? selected, string fallback)
    {
        if (selected is not TimeSpan ts) return fallback;
        return new TimeOnly(ts.Hours, ts.Minutes).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // =====================================================================
    //  FOLDER ADD / REMOVE
    // =====================================================================
    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.FolderDialog { XamlRoot = Content.XamlRoot, WindowHandle = WindowHandle };
        var result = await ShowDialogAsync(dlg);
        if (result == ContentDialogResult.Primary)
            _cfg.Folders.Add(new FolderPair(true, dlg.SourcePath, dlg.SubFolder));
    }

    private async void OnEditFolder(object sender, RoutedEventArgs e)
    {
        var f = _currentFolder;
        if (f == null)
        {
            await ShowMessageAsync("GUARD", "Highlight the folder you want to edit from the folder list, then press Edit Folder.");
            return;
        }
        var dlg = new Views.FolderDialog { XamlRoot = Content.XamlRoot, WindowHandle = WindowHandle };
        dlg.LoadFolder(f);
        if (await ShowDialogAsync(dlg) == ContentDialogResult.Primary)
        {
            // Update the existing item rather than replacing it: its property
            // change notifications refresh the bound row in place and flow into
            // dirty tracking via OnFolderItemChanged, and the row keeps its
            // position and focus memory.
            f.Source = dlg.SourcePath;
            f.SubFolder = dlg.SubFolder;
        }
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        var f = _currentFolder;
        if (f == null)
        {
            await ShowMessageAsync("GUARD", "Highlight the folder you want to remove from the folder list, then press Remove Folder.");
            return;
        }
        // Spell out the untick alternative: Remove forgets the pair entirely,
        // while unticking keeps it listed but out of the generated script.
        if (await ShowConfirmAsync("GUARD",
            "Remove this folder from the list entirely?\n\n" + f.Source +
            "\n\nIf you only want to skip it for now, choose No and untick it instead."))
        {
            _cfg.Folders.Remove(f);
            _currentFolder = null;
        }
    }

    // =====================================================================
    //  EXCLUSION ADD / REMOVE
    // =====================================================================
    private async void OnAddExclude(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.ExcludeDialog { XamlRoot = Content.XamlRoot };
        var result = await ShowDialogAsync(dlg);
        if (result == ContentDialogResult.Primary)
            _cfg.Excludes.Add(new ExcludeItem(dlg.IsFolder, dlg.Pattern));
    }

    private async void OnRemoveExclude(object sender, RoutedEventArgs e)
    {
        if (ExcludeList.SelectedItem is not ExcludeItem x)
        {
            await ShowMessageAsync("GUARD", "Select the exclusion you want to remove in the custom exclusions list, then press Remove Exclusion.");
            return;
        }
        if (await ShowConfirmAsync("GUARD", "Remove this exclusion?\n\n" + x.Caption))
            _cfg.Excludes.Remove(x);
    }

    // =====================================================================
    //  APP SCAN
    // =====================================================================
    private void OnRefreshApps(object sender, RoutedEventArgs e) { _appScanned = true; ScanApps(announceStart: true); }

    private void ScanApps(bool announceStart)
    {
        if (_scanning) return;
        _scanning = true;
        SetAppBusy(true);
        _appStatusText = "Scanning installed apps (this can take a few seconds)...";
        // A tab-switch scan shows this silently (the tab selection is already
        // being announced); an explicit Refresh announces it, since nothing else
        // is speaking then. Either way the completion summary is announced below.
        if (announceStart) AnnounceAppStatus(); else UpdateStatusBar();

        var th = new Thread(() =>
        {
            ScanResult? res = null; string? err = null;
            try { res = AppInventory.DetectApps(); }
            catch (Exception ex) { err = ex.Message; }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (err != null) { _appStatusText = "Scan failed: " + err; }
                else if (res != null)
                {
                    _wingetAvailable = res.WingetAvailable;
                    _allApps.Clear();
                    _allApps.AddRange(res.Apps);
                    int auto = 0, man = 0;
                    foreach (var a in res.Apps) { if (a.CanAuto) auto++; else man++; }
                    if (_wingetAvailable)
                        _appStatusText = res.Apps.Count + " apps found. " + auto + " reinstallable via winget, " + man + " manual.";
                    else
                        _appStatusText = res.Apps.Count + " apps found. winget is not installed, so apps cannot be reinstalled automatically. You can still export the list for reference.";
                    ApplyFilter();
                }
                _scanning = false; SetAppBusy(false);
                AnnounceAppStatus();
            });
        }) { IsBackground = true };
        th.Start();
    }

    private void SetAppBusy(bool busy)
    {
        bool e = !busy;
        BtnAppRefresh.IsEnabled = e;
        BtnAppExport.IsEnabled = e;
        BtnAppImport.IsEnabled = e;
        ChkExportSettings.IsEnabled = e;
        BtnAppAll.IsEnabled = e;
        BtnAppNone.IsEnabled = e;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _appFilter = (TxtAppFilter.Text ?? "").Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        AppRows.Clear();
        string f = _appFilter.ToLowerInvariant();
        foreach (var a in _allApps)
        {
            if (f.Length == 0 || MatchesFilter(a, f))
                AppRows.Add(a);
        }
        UpdateAppCount();
    }

    private void UpdateAppCount()
    {
        if (LblAppCount == null) return;
        string text = _appFilter.Length == 0
            ? Plural(_allApps.Count)
            : AppRows.Count + " of " + Plural(_allApps.Count);
        if (text == LblAppCount.Text) return;
        LblAppCount.Text = text;
        // Announce the new count only while the user is typing in the filter box
        // (where it is the immediate feedback they need); a scan or import already
        // announces its own summary through the status bar, so speaking the count
        // there too would double up. Identical counts across keystrokes stay silent
        // because the text has not changed.
        if (TxtAppFilter.FocusState != FocusState.Unfocused)
            Announce(LblAppCount);
    }

    private static string Plural(int n) => n == 1 ? "1 app" : n + " apps";

    private static bool MatchesFilter(AppEntry a, string f)
    {
        if (Contains(a.Name, f) || Contains(a.Publisher, f) || Contains(a.Id, f) || Contains(a.SourceLabel, f))
            return true;

        // Type aliases not covered by SourceLabel
        return a.Source switch
        {
            "manual"  => f is "installer",
            "msstore" => f is "msstore" or "ms store" or "microsoft store",
            _         => false,
        };
    }

    private static bool Contains(string? s, string f) =>
        !string.IsNullOrEmpty(s) && s.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) >= 0;

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var a in AppRows) a.Include = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var a in AppRows) a.Include = false; }

    // =====================================================================
    //  EXPORT / IMPORT
    // =====================================================================
    // One Export action covers both outputs: the app-list JSON always, plus
    // the ticked apps' settings folders when "Also export app settings" is
    // ticked. The settings step runs first because it is the only part the
    // user can cancel, and cancelling must abort the whole export - a Cancel
    // that still wrote the list would be a surprise partial result (untick
    // the option to export the list alone).
    private async void OnExportApps(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            await ShowMessageAsync("GUARD", "An export is already running. Wait for it to finish.");
            return;
        }
        HarvestUi();
        if (string.IsNullOrEmpty(_cfg.AppListDest))
        {
            await ShowMessageAsync("GUARD", "Enter an app list destination first.\n\nType a folder path next to \"List destination\", or use the Browse button to pick one.");
            return;
        }
        var picked = new List<AppEntry>();
        foreach (var a in _allApps) if (a.Include) picked.Add(a);
        if (picked.Count == 0)
        {
            await ShowMessageAsync("GUARD", "Tick at least one app to export.");
            return;
        }
        try { if (!Directory.Exists(_cfg.AppListDest)) Directory.CreateDirectory(_cfg.AppListDest); }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "Destination is not reachable:\n" + _cfg.AppListDest + "\n\n" + ex.Message);
            return;
        }

        _exporting = true;
        SetAppBusy(true);
        try
        {
            // ---- Settings confirmation step (only when opted in) ----
            bool wantSettings = _cfg.ExportAppSettings;
            bool noMatches = false;
            List<AppSettingsCandidate>? chosen = null;
            if (wantSettings)
            {
                // Progress slot (not the main line), with an indeterminate bar
                // while the disk is walked. Focus has not moved yet, so the
                // spoken cue is a plain (undelayed) UIA notification.
                SetExportProgress("Looking for settings folders for " + picked.Count + " ticked app(s)...", indeterminate: true);
                AnnounceNotification(AppProgressLabel, "Looking for settings folders for " + picked.Count + " app(s)...");

                // Candidate matching walks the disk, so it runs off the UI
                // thread; sizes are measured later, inside the open dialog.
                var candidates = await System.Threading.Tasks.Task.Run(
                    () => AppSettingsExport.FindCandidates(picked));
                if (candidates.Count == 0)
                {
                    // Nothing to confirm and nothing the user can act on here;
                    // the list export still goes ahead and the summary says
                    // why no settings folders came with it.
                    noMatches = true;
                }
                else
                {
                    // The matching is heuristic; nothing is copied until the
                    // user has confirmed (and could untick) every match.
                    var dlg = new Views.AppSettingsDialog(candidates) { XamlRoot = Content.XamlRoot };
                    if (await ShowDialogAsync(dlg) != ContentDialogResult.Primary)
                    {
                        // Settled: the dialog just closed (a focus change) which
                        // would otherwise cut the cancellation message off.
                        SetExportOutcome("Export cancelled. Nothing was exported.");
                        AnnounceSettled(AppProgressLabel, "Export cancelled. Nothing was exported.");
                        return;
                    }
                    chosen = new List<AppSettingsCandidate>();
                    foreach (var c in candidates) if (c.Include) chosen.Add(c);
                }
            }

            // ---- Export folder: one per export, so the app list and its
            // settings are always paired and repeated exports never overwrite
            // each other. The folder name carries the uniqueness, so the list
            // inside is always app-list.json (no numbered names). ----
            var file = new AppListFile
            {
                Exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Machine = Environment.MachineName
            };
            var items = new List<AppListItem>();
            foreach (var a in picked)
                items.Add(new AppListItem
                {
                    Name = a.Name, Id = a.Id, Source = a.Source, Version = a.Version,
                    Publisher = a.Publisher, InstallLocation = a.InstallLocation, PublisherUrl = a.PublisherUrl
                });
            file.Apps = items.ToArray();

            string exportDir = MakeUniqueExportDir(_cfg.AppListDest);
            Directory.CreateDirectory(exportDir);
            string path = Path.Combine(exportDir, GuardPaths.AppListFileName);
            AppListIo.Write(path, file);

            // ---- Settings copy (after the list, so a cancel above never
            // leaves either output behind) ----
            string summary = "Exported " + picked.Count + " app(s) to " + Path.GetFileName(exportDir) + ".";
            string detail = "Exported " + picked.Count + " apps to:\n" + path;
            if (wantSettings)
            {
                if (noMatches)
                {
                    string why = " No settings folders matched the ticked apps. Apps that keep settings in the registry, in ProgramData, or under a folder name unlike the app name are not found by this search.";
                    summary += why;
                    detail += "\n" + why.Trim();
                }
                else if (chosen!.Count == 0)
                {
                    summary += " No settings folders were ticked, so none were copied.";
                    detail += "\n\nNo settings folders were ticked, so none were copied.";
                }
                else
                {
                    // A determinate progress bar (by bytes, from the sizes already
                    // measured) plus a spoken first line: a large folder - an
                    // Electron app's profile, say - otherwise copies with no
                    // feedback between the per-folder lines and looks frozen, with
                    // no screen-reader cue at all. Per convention only the first
                    // line is spoken; the bar carries the rest and the summary is
                    // read by the dialog that follows.
                    long totalBytes = 0;
                    foreach (var c in chosen) totalBytes += c.Bytes;
                    double barMax = totalBytes > 0 ? totalBytes : 1;
                    StatusBarProgress.IsIndeterminate = false;
                    AppProgress.IsIndeterminate = false;
                    SetProgress(AppProgress, AppProgressLabel, barMax, 0, "Copying settings...");
                    bool announced = false;
                    var stats = await System.Threading.Tasks.Task.Run(() =>
                        AppSettingsExport.CopyCandidates(chosen, exportDir,
                            onFolder: msg => DispatcherQueue.TryEnqueue(() =>
                            {
                                AppProgressLabel.Text = msg;
                                StatusBarProgressText.Text = msg;
                                if (!announced) { announced = true; AnnounceSettled(AppProgressLabel, msg); }
                            }),
                            onBytes: done => DispatcherQueue.TryEnqueue(() =>
                            {
                                AppProgress.Value = done;
                                StatusBarProgress.Value = done;
                            })));
                    string copied = "Copied " + stats.Folders + " settings folder(s): " + stats.Files + " file(s)."
                        + (stats.SkippedFiles > 0
                            ? " " + stats.SkippedFiles + " file(s) were locked or unreadable and were skipped."
                            : "");
                    summary += " " + copied;
                    detail += "\n\n" + copied
                        + "\nThe settings are inside this export's folder. To restore later, use "
                        + "Import List and pick this app-list.json.";
                }
            }

            SettingsStore.Save(_cfg);
            // Outcome goes in the progress slot (like a backup/reinstall outcome),
            // not the main line: it is not announced here (the dialog below reads
            // it), and the main line keeps the inventory status so reading the bar
            // does not say the same thing twice.
            SetExportOutcome(summary);
            await ShowMessageAsync("GUARD", detail);
        }
        catch (Exception ex)
        {
            SetExportOutcome("Export failed: " + ex.Message);
            await ShowMessageAsync("GUARD", "Export failed:\n" + ex.Message);
        }
        finally
        {
            // Never leave a spinning/indeterminate bar behind on any exit path.
            StatusBarProgress.IsIndeterminate = false;
            AppProgress.IsIndeterminate = false;
            _exporting = false;
            SetAppBusy(false);
        }
    }

    // A unique, sortable folder for one export under the chosen destination, so
    // each export's list and settings stay paired and repeats never collide.
    private static string MakeUniqueExportDir(string dest)
    {
        string baseName = "app-export-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        string dir = Path.Combine(dest, baseName);
        for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(dest, baseName + "-" + n);
        return dir;
    }

    // Export status is shown in the status bar's progress slot, never the main
    // status line, so reading the bar never says the same thing twice (the main
    // line keeps the persistent inventory status, exactly as it does during a
    // backup or reinstall). The slot is not a live region; spoken cues are
    // raised separately via AnnounceNotification / AnnounceSettled.
    private void SetExportProgress(string text, bool indeterminate)
    {
        ShowStatusBarProgress(true);
        StatusBarProgress.IsIndeterminate = indeterminate;
        AppProgress.IsIndeterminate = indeterminate;
        StatusBarProgressText.Text = text;
        AppProgressLabel.Text = text;
    }

    // Terminal export state (success summary, cancellation, failure): show it in
    // the progress slot with no moving bar, and keep the slot visible so the
    // read-status-bar hotkey reports the outcome from anywhere.
    private void SetExportOutcome(string text)
    {
        StatusBarProgress.IsIndeterminate = false;
        AppProgress.IsIndeterminate = false;
        StatusBarProgress.Visibility = Visibility.Collapsed;
        StatusBarProgressArea.Visibility = Visibility.Visible;
        StatusBarProgressText.Text = text;
        AppProgressLabel.Text = text;
    }

    private async void OnImportApps(object sender, RoutedEventArgs e)
    {
        HarvestUi();
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        AppListFile? f;
        try { f = AppListIo.Read(file.Path); }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "That file could not be read as an app list:\n\n" + ex.Message);
            return;
        }
        if (f?.Apps == null || f.Apps.Length == 0)
        {
            await ShowMessageAsync("GUARD", "The app list is empty.");
            return;
        }

        // The imported apps live in their OWN list (this dialog), so the
        // installed-apps list behind the tab - the source for Export - is left
        // untouched. _allApps and the main list are deliberately not modified.
        var imported = new List<AppEntry>();
        int auto = 0, man = 0;
        foreach (var it in f.Apps)
        {
            var a = new AppEntry
            {
                Name = it.Name ?? "",
                Id = it.Id ?? "",
                Source = string.IsNullOrEmpty(it.Source) ? (string.IsNullOrEmpty(it.Id) ? "manual" : "winget") : it.Source!,
                Version = it.Version ?? "",
                Publisher = it.Publisher ?? "",
                InstallLocation = it.InstallLocation ?? "",
                PublisherUrl = it.PublisherUrl ?? "",
                Include = true
            };
            if (a.CanAuto) auto++; else man++;
            imported.Add(a);
        }

        // Settings bundle written beside the list (sibling AppSettings folder +
        // manifest)? Its presence enables the dialog's Reinstall & Restore option.
        string listDir = Path.GetDirectoryName(file.Path) ?? "";
        var manifest = AppSettingsRestore.TryLoadBundle(listDir);

        string mac = f.Machine ?? "?";
        string exp = f.Exported ?? "";
        string header = imported.Count + " app(s) from " + mac +
            (exp.Length > 0 ? " (" + exp + ")" : "") + ". " + auto + " reinstallable, " + man + " manual." +
            (manifest != null
                ? " A settings bundle was found beside this list."
                : " No settings bundle was found beside this list.");

        var dlg = new Views.AppImportDialog(imported, header, manifest != null) { XamlRoot = Content.XamlRoot };
        var result = await ShowDialogAsync(dlg);
        if (result == ContentDialogResult.None)
        {
            _appStatusText = "Import cancelled. Nothing was changed.";
            AnnounceAppStatus();
            return;
        }

        // Ticked winget/store apps become reinstall targets; the rest are counted
        // as manual (skipped by winget, but their settings can still be restored).
        var targets = new List<AppEntry>();
        int manual = 0;
        foreach (var a in imported)
        {
            if (!a.Include) continue;
            if (a.CanAuto && !string.IsNullOrEmpty(a.Id)) targets.Add(a);
            else manual++;
        }

        // Primary = Reinstall Selected; Secondary = Reinstall & Restore Settings.
        if (result == ContentDialogResult.Primary)
        {
            if (targets.Count == 0)
            {
                await ShowMessageAsync("GUARD", "None of the ticked apps can be reinstalled automatically.\n\nOnly \"Winget\" and \"Store\" apps reinstall automatically; \"Manual\" apps must be reinstalled by hand.");
                return;
            }
            await ExecuteReinstall(targets, restore: null, BtnAppImport);
            return;
        }

        // ---- Reinstall & Restore Settings ----
        var candidates = AppSettingsRestore.BuildCandidates(manifest!, listDir);
        if (candidates.Count == 0)
        {
            // The manifest names folders that are no longer on disk beside the
            // list; fall back to reinstalling the apps alone.
            if (targets.Count == 0)
            {
                await ShowMessageAsync("GUARD", "The settings folders named by this list could not be found beside it, and none of the ticked apps reinstall automatically. Nothing to do.");
                return;
            }
            await ShowMessageAsync("GUARD", "The settings folders named by this list could not be found beside it, so only the apps will be reinstalled.\n\nKeep the AppSettings folder next to the list file to restore settings.");
            await ExecuteReinstall(targets, restore: null, BtnAppImport);
            return;
        }

        var restoreHeader = new System.Text.StringBuilder();
        if (targets.Count > 0)
        {
            restoreHeader.Append("GUARD will reinstall " + targets.Count + " app(s) via winget, one at a time");
            if (manual > 0) restoreHeader.Append(", skipping " + manual + " ticked \"Manual\" app(s)");
            restoreHeader.Append(", then restore the ticked settings folders below. ");
        }
        else
        {
            restoreHeader.Append("None of the ticked apps reinstall automatically, so GUARD will only restore the ticked settings folders below. ");
        }
        restoreHeader.Append("A folder marked \"Replaces existing\" already exists on this PC; your current copy is renamed aside (.guard-old-) before it is replaced, so you can undo.");

        var sdlg = new Views.AppSettingsRestoreDialog(candidates, restoreHeader.ToString())
        {
            XamlRoot = Content.XamlRoot,
            Title = targets.Count > 0 ? "Reinstall and Restore App Settings" : "Restore App Settings",
            PrimaryButtonText = targets.Count > 0 ? "Reinstall & Restore" : "Restore",
        };
        if (await ShowDialogAsync(sdlg) != ContentDialogResult.Primary)
        {
            _appStatusText = "Reinstall cancelled. Nothing was changed.";
            AnnounceAppStatus();
            return;
        }

        var chosen = new List<AppSettingsRestoreCandidate>();
        foreach (var c in candidates) if (c.Include) chosen.Add(c);
        if (targets.Count == 0 && chosen.Count == 0)
        {
            _appStatusText = "Nothing was selected to reinstall or restore.";
            AnnounceAppStatus();
            return;
        }

        await ExecuteReinstall(targets, chosen.Count > 0 ? chosen : null, BtnAppImport);
    }

    // =====================================================================
    //  REINSTALL
    // =====================================================================
    // Runs the winget install phase and then, when a restore set is given, the
    // settings-restore phase - as one cancellable job under the run-feedback
    // focus discipline. Launched from the Import List dialog (see OnImportApps),
    // not a tab button, so it acts on the saved list rather than the installed
    // apps. launcherButton is the button that opened that dialog, so focus can
    // return there when the job ends. restore is null for an apps-only run.
    private async System.Threading.Tasks.Task ExecuteReinstall(
        List<AppEntry> targets, List<AppSettingsRestoreCandidate>? restore, Control launcherButton)
    {
        // Belt-and-suspenders: SetAppBusy already disables Import while a run is
        // live, so this path should be unreachable during one.
        if (_reinstalling) return;
        _reinstalling = true;
        _reinstallCts = new CancellationTokenSource();
        var ct = _reinstallCts.Token;
        // Same focus discipline as SetFileBusy: enable Stop and hand it focus
        // before SetAppBusy greys out the button that launched the job, so
        // focus never gets thrown at an arbitrary neighbour.
        BtnAppStop.IsEnabled = true;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
        Control? launcher = ReferenceEquals(focused, launcherButton) ? launcherButton : null;
        if (launcher != null) BtnAppStop.Focus(FocusState.Programmatic);
        SetAppBusy(true);
        TxtAppOutput.Text = "";
        int restoreCount = restore?.Count ?? 0;
        int totalSteps = targets.Count + restoreCount;
        SetProgress(AppProgress, AppProgressLabel, totalSteps > 0 ? totalSteps : 1, 0, "Starting...");
        ShowStatusBarProgress(true);

        int ok = 0, fail = 0, attempted = 0;
        AppSettingsRestoreStats? rstats = null;
        await System.Threading.Tasks.Task.Run(() =>
        {
            // ---- Install phase ----
            for (int i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var app = targets[i];
                string installing = "Installing: " + app.Name + " (" + (i + 1) + " of " + targets.Count + ")";
                SetProgress(AppProgress, AppProgressLabel, totalSteps, i, installing);
                // First item only, like the backup run's start announcement.
                if (i == 0) AnnounceSettled(AppProgressLabel, installing);
                AppendOut(TxtAppOutput, "\r\n=== Installing " + app.Name + "  [" + app.Id + "] ===\r\n");
                int code;
                try { code = ProcessRunner.RunWingetInstall(app.Id, s => AppendOut(TxtAppOutput, s), ct); }
                catch (Exception ex) { AppendOut(TxtAppOutput, "ERROR: " + ex.Message + "\r\n"); code = -1; }
                // A cancel mid-install kills winget, which surfaces as a nonzero
                // exit code; do not count a killed item as a failure.
                if (ct.IsCancellationRequested) break;
                attempted++;
                if (code == 0) ok++; else fail++;
                SetProgress(AppProgress, AppProgressLabel, totalSteps, attempted, "");
            }

            // ---- Restore phase (after the installs, so an app's freshly
            // installed defaults are overwritten with the restored settings, and
            // a cancel during installs skips restore entirely). Per-folder
            // progress updates the bar silently, like the export copy. ----
            if (!ct.IsCancellationRequested && restore != null)
            {
                AppendOut(TxtAppOutput, "\r\n=== Restoring app settings ===\r\n");
                int baseStep = targets.Count;
                int done = 0;
                rstats = AppSettingsRestore.RestoreCandidates(restore, msg =>
                {
                    int step = baseStep + done;
                    done++;
                    SetProgress(AppProgress, AppProgressLabel, totalSteps, step, msg);
                    AppendOut(TxtAppOutput, msg + "\r\n");
                    // When there is no install phase, the restore start is the
                    // job's first spoken line (the install loop spoke otherwise).
                    if (targets.Count == 0 && step == 0) AnnounceSettled(AppProgressLabel, msg);
                }, ct);
            }
        });

        // Back on the UI thread; the DispatcherQueue is FIFO, so everything the
        // worker enqueued (output, progress) has already landed by now and the
        // summary below always prints last.
        string outcome = BuildReinstallOutcome(ct.IsCancellationRequested, targets.Count, attempted, ok, fail, rstats);
        AppProgressLabel.Text = outcome;
        StatusBarProgressText.Text = outcome;
        AppendOut(TxtAppOutput, "\r\n--- " + outcome + " ---\r\n");

        _reinstalling = false;
        _reinstallCts.Dispose();
        _reinstallCts = null;
        // Re-enable the launchers and put focus back on the launcher before Stop
        // greys out, then announce last (after the focus events) so the focus
        // announcement cannot cancel the summary speech.
        SetAppBusy(false);
        if (launcher != null && ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), BtnAppStop))
            launcher.Focus(FocusState.Programmatic);
        BtnAppStop.IsEnabled = false;
        ShowStatusBarProgress(false);
        AnnounceSettled(AppProgressLabel, outcome, 2000);
    }

    // The end-of-job line, covering whichever phases ran: a cancelled run
    // reports how far the installs got; a completed run reports install counts
    // (when any) and the restore tally (when settings were restored).
    private static string BuildReinstallOutcome(
        bool cancelled, int targetCount, int attempted, int ok, int fail, AppSettingsRestoreStats? rstats)
    {
        if (cancelled)
            return "Cancelled after " + attempted + " of " + targetCount + " app(s): " + ok +
                " installed, " + fail + " failed. Apps already installed stay installed.";

        string s = "";
        if (targetCount > 0)
            s = "Done. " + ok + " installed, " + fail + " failed.";
        if (rstats != null)
        {
            string r = "Restored " + rstats.Folders + " settings folder(s)"
                + (rstats.Replaced > 0 ? " (" + rstats.Replaced + " existing folder(s) kept aside)" : "") + ".";
            if (rstats.SkippedFolders > 0)
                r += " " + rstats.SkippedFolders + " folder(s) were in use and skipped.";
            if (rstats.SkippedFiles > 0)
                r += " " + rstats.SkippedFiles + " file(s) were locked and skipped.";
            s = s.Length > 0 ? s + " " + r : r;
        }
        return s.Length > 0 ? s : "Done.";
    }

    private void OnStopReinstall(object sender, RoutedEventArgs e) => _reinstallCts?.Cancel();

    // =====================================================================
    //  RUN SCRIPT
    // =====================================================================
    private async void OnRunNow(object sender, RoutedEventArgs e) => await RunScript("");
    private async void OnPreview(object sender, RoutedEventArgs e) => await RunScript("test");

    private async System.Threading.Tasks.Task RunScript(string arg)
    {
        if (_backupRunning)
        {
            await ShowMessageAsync("GUARD", "A backup is already running. Wait for it to finish, or press Stop Backup to cancel it.");
            return;
        }
        if (!await SaveAllAsync()) return;
        string script = GuardPaths.ScriptPath;
        if (!File.Exists(script))
        {
            await ShowMessageAsync("GUARD", "Script not found:\n" + script);
            return;
        }

        TxtOutput.Text = "";
        AppendOut(TxtOutput, "> " + Path.GetFileName(script) + (arg.Length > 0 ? " " + arg : "") + "\r\n");
        // A modal here would interrupt the run the user just asked for; the
        // script SKIPs unreachable sources itself, so a line in the output is
        // the right weight.
        if (_missingSources.Count > 0)
            AppendOut(TxtOutput, "WARNING: " + DescribeMissingSources(_missingSources).Replace("\n", "\r\n  ")
                + "\r\nThey will be skipped if still unreachable.\r\n");
        _progTotal = 0;
        _summaryParser = new RobocopySummaryParser();
        _runIsPreview = arg == "test";
        _runDoneAnnounce = null;
        SetProgress(FileProgress, FileProgressLabel, 1, 0, "");
        ShowStatusBarProgress(true);

        _backupRunning = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        SetFileBusy(true);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + script + "\" " + arg + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = GuardPaths.BaseDir
            };
            psi.EnvironmentVariables["GUARD_UI"] = "1";
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, ev) => HandleScriptLine(ev.Data);
            proc.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendOut(TxtOutput, ev.Data + "\r\n"); };
            proc.Start();
            // Cancel kills the whole tree: cmd.exe alone would die while its
            // robocopy child kept copying. Kill throws if the process already
            // exited naturally in the same instant; that race is harmless.
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.StandardInput.Close();
            await proc.WaitForExitAsync();
            // Parameterless WaitForExit additionally drains the async output
            // handlers, so the completion line below always lands after the
            // script's own last output.
            proc.WaitForExit();
            if (ct.IsCancellationRequested)
            {
                AppendOut(TxtOutput, "\r\n--- cancelled by user ---\r\n");
                // Enqueued (not set directly) so it lands after any progress
                // update the output handlers enqueued during the drain above;
                // a direct set could be overwritten by a stale "Backing up"
                // line still sitting in the queue.
                DispatcherQueue.TryEnqueue(() =>
                {
                    FileProgressLabel.Text = "Backup cancelled.";
                    // Mirror into the bar's outcome slot; SetProgress last wrote
                    // a stale "Backing up..." line there.
                    StatusBarProgressText.Text = "Backup cancelled.";
                });
            }
            else
            {
                AppendOut(TxtOutput, "\r\n--- finished ---\r\n");
            }
        }
        catch (Exception ex)
        {
            AppendOut(TxtOutput, "ERROR launching script: " + ex.Message + "\r\n");
        }
        finally
        {
            // Whatever the outcome (finish, cancel, launch error), the bar's
            // progress area must not outlive the job it mirrors.
            ShowStatusBarProgress(false);
            _backupRunning = false;
            _runCts.Dispose();
            _runCts = null;
            SetFileBusy(false);
        }
        string? spoken = ct.IsCancellationRequested ? "Backup cancelled." : _runDoneAnnounce;
        if (spoken != null) AnnounceSettled(FileProgressLabel, spoken, 2000);
    }

    private void OnStopBackup(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    // Lock out the actions that conflict with a running backup. Save Settings is
    // included because it rewrites guard-backup.cmd, and cmd.exe reads batch
    // files incrementally, so rewriting one mid-run corrupts the run. The Stop
    // button is the inverse: only operable while something is running.
    // The button that launched the current backup run, so focus can return to
    // it when the run ends. Focus is managed explicitly around the enable /
    // disable flips: disabling a focused button lets WinUI throw focus at an
    // arbitrary neighbour (it landed on Open Last Log), and the screen
    // reader's announcement of that surprise focus cancels whatever was being
    // spoken - which ate the end-of-run summary.
    private Control? _fileRunLauncher;

    private void SetFileBusy(bool busy)
    {
        if (busy)
        {
            // Enable Stop and hand it focus before the launchers grey out;
            // Stop is the one action available during the run.
            BtnStopBackup.IsEnabled = true;
            _fileRunLauncher = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
            if (_fileRunLauncher == BtnSave || _fileRunLauncher == BtnRunNow || _fileRunLauncher == BtnPreview)
                BtnStopBackup.Focus(FocusState.Programmatic);
            else
                _fileRunLauncher = null;
            BtnSave.IsEnabled = false;
            BtnRunNow.IsEnabled = false;
            BtnPreview.IsEnabled = false;
        }
        else
        {
            BtnSave.IsEnabled = true;
            BtnRunNow.IsEnabled = true;
            BtnPreview.IsEnabled = true;
            // Return focus to the launcher before Stop greys out, so rerunning
            // is one keypress away and focus never lands somewhere arbitrary.
            if (_fileRunLauncher != null &&
                ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), BtnStopBackup))
                _fileRunLauncher.Focus(FocusState.Programmatic);
            _fileRunLauncher = null;
            BtnStopBackup.IsEnabled = false;
        }
    }

    private void HandleScriptLine(string? data)
    {
        if (data == null) return;
        if (data.StartsWith("@@PROGRESS@@"))
        {
            string rest = data.Substring("@@PROGRESS@@".Length).Trim();
            if (rest == "DONE")
            {
                // The DONE marker is emitted after every robocopy call, so all
                // summary blocks have been fed by now. A null summary (nothing
                // parsed) keeps the original completion message untouched.
                string done = (_runIsPreview ? "Preview" : "Backup") +
                    " complete (" + _progTotal + " of " + _progTotal + ").";
                string? summary = null;
                try { summary = BuildRunSummary(); } catch { }
                if (summary != null)
                {
                    done = summary;
                    AppendOut(TxtOutput, "\r\n" + summary + "\r\n");
                }
                SetProgress(FileProgress, FileProgressLabel, _progTotal > 0 ? _progTotal : 1, _progTotal, done);
                // Stashed, not announced here: the announcement waits until
                // RunScript has finished its end-of-run focus handling, or the
                // focus change would cancel the speech mid-summary.
                _runDoneAnnounce = done;
                return;
            }
            var m = Regex.Match(rest, "^(\\d+)\\s+(\\d+)\\s*(.*)$");
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                int tot = int.Parse(m.Groups[2].Value);
                string nm = m.Groups[3].Value.Trim();
                _progTotal = tot;
                string prog = "Backing up: " + nm + " (" + n + " of " + tot + ")";
                SetProgress(FileProgress, FileProgressLabel, tot, n - 1, prog);
                // Speak the first progress line so a screen-reader user hears
                // the run actually begin; the rest of the stream stays silent
                // (a per-folder announcement stream would be noisy).
                if (n == 1) AnnounceSettled(FileProgressLabel, prog);
            }
            return;
        }
        // Summary parsing must never break run handling; on any parser fault the
        // run degrades to the plain completion message.
        try { _summaryParser?.Feed(data); } catch { _summaryParser = null; }
        AppendOut(TxtOutput, data + "\r\n");
    }

    // Builds the human-readable end-of-run summary from the accumulated robocopy
    // tables, or null when nothing parsed (parse failure, zero folders, or a
    // localized table the parser did not recognise).
    private string? BuildRunSummary()
    {
        var p = _summaryParser;
        if (p == null || p.Blocks == 0) return null;
        bool mirror = _cfg.Mode == "Mirror";
        string copied = CountPhrase(p.FilesCopied, "file");
        string bytes = FormatBytes(p.BytesCopied);
        if (bytes.Length > 0 && p.FilesCopied > 0) copied += " (" + bytes + ")";
        string skipped = p.FilesSkipped.ToString("N0", CultureInfo.CurrentCulture);

        if (p.FilesFailed > 0)
        {
            // Failures lead so a screen reader hears the problem first.
            string failed = CountPhrase(p.FilesFailed, "file");
            return _runIsPreview
                ? "Preview finished with problems: " + failed + " could not be read - open the last log for details. " +
                  copied + " would be copied, " + skipped + " already up to date."
                : "Backup finished with problems: " + failed + " failed to copy - open the last log for details. " +
                  copied + " copied, " + skipped + " skipped.";
        }

        string extras = "";
        if (p.FilesExtras > 0)
        {
            string ex = CountPhrase(p.FilesExtras, "extra file");
            // Extras are only deleted in Mirror mode; in Additive they just sit
            // in the destination, so the wording must not claim a removal.
            extras = _runIsPreview
                ? (mirror ? " " + ex + " would be removed from the backup." : " " + ex + " found in the backup.")
                : (mirror ? " " + ex + " removed from the backup." : " " + ex + " found in the backup.");
        }

        return _runIsPreview
            ? "Preview complete: " + copied + " would be copied, " + skipped + " already up to date." + extras
            : "Backup complete: " + copied + " copied, " + skipped + " skipped (already up to date)." + extras;
    }

    private static string CountPhrase(long n, string noun)
        => n.ToString("N0", CultureInfo.CurrentCulture) + " " + noun + (n == 1 ? "" : "s");

    private static string FormatBytes(double b)
    {
        if (b <= 0) return "";
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return (u == 0 ? b.ToString("N0", CultureInfo.CurrentCulture)
                       : b.ToString("0.#", CultureInfo.CurrentCulture)) + " " + units[u];
    }

    private void SetProgress(ProgressBar bar, TextBlock lbl, double max, double val, string text)
    {
        if (bar == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (max > 0) bar.Maximum = max;
            bar.Value = val;
            if (lbl != null) lbl.Text = text;
            // Mirror into the status bar so the running job stays visible when
            // the in-tab progress area is scrolled away or on the other tab.
            // Deliberately not a live region: progress text is not announced
            // today and a per-item announcement stream would be noisy.
            if (max > 0) StatusBarProgress.Maximum = max;
            StatusBarProgress.Value = val;
            StatusBarProgressText.Text = text;
        });
    }

    // The bar's progress area shows live progress while a job runs; when the
    // job ends only the progress bar hides, and the area keeps the final
    // outcome message (summary / cancelled / done counts) until the next job,
    // so the read-status-bar hotkey reports how the last run ended from
    // anywhere in the app. The area only collapses entirely when there is no
    // outcome to show (before any job, or after a launch failure).
    private void ShowStatusBarProgress(bool show)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusBarProgress.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
                StatusBarProgressArea.Visibility = Visibility.Visible;
            else if (string.IsNullOrEmpty(StatusBarProgressText.Text))
                StatusBarProgressArea.Visibility = Visibility.Collapsed;
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
    // scrolls a WinUI 3 TextBox while it has focus, so drive the ScrollViewer
    // inside the TextBox template directly. ChangeView (animation disabled) moves
    // the viewport without taking keyboard focus and without raising any focus or
    // live-region automation event, so a screen reader's reading position is not
    // disturbed beyond the text change itself. UpdateLayout first so
    // ScrollableHeight reflects the line just appended.
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
    private void OnOpenLog(object sender, RoutedEventArgs e) => OpenPath(GuardPaths.LogPath);
    private void OnOpenDest(object sender, RoutedEventArgs e) => OpenPath(TxtDest.Text);
    private void OnOpenAppDest(object sender, RoutedEventArgs e) => OpenPath(TxtAppDest.Text);

    private async System.Threading.Tasks.Task BrowseInto(TextBox box)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) box.Text = folder.Path;
    }

    private async System.Threading.Tasks.Task TestConnection(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) { await ShowMessageAsync("GUARD", "Enter a destination path first."); return; }
        try
        {
            if (Directory.Exists(path)) { await ShowMessageAsync("GUARD", "Reachable:\n" + path); return; }
            Directory.CreateDirectory(path);
            await ShowMessageAsync("GUARD", "Created and reachable:\n" + path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "Not reachable:\n" + path + "\n\n" + ex.Message);
        }
    }

    private async void OpenPath(string? path)
    {
        try
        {
            path = (path ?? "").Trim();
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                await ShowMessageAsync("GUARD", "Not found:\n" + path);
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
    }

    // =====================================================================
    //  DIALOG HELPERS
    // =====================================================================
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

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

    // =====================================================================
    //  CLOSE GUARD
    // =====================================================================
    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        bool busy = _backupRunning || _reinstalling;
        if (!busy) return;

        args.Cancel = true;
        string what = _reinstalling ? "An app reinstall is still running." : "A backup is still running.";
        if (await ShowConfirmAsync("GUARD", what + " Close anyway?"))
        {
            // Cancel both jobs so no cmd/robocopy/winget tree outlives the
            // window; the kill registrations run synchronously inside Cancel.
            _runCts?.Cancel();
            _reinstallCts?.Cancel();
            _allowClose = true;
            Close();
        }
    }
}
