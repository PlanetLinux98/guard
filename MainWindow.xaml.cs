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
    private long _imageLogPos;
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
        // Wider than the old 820 to keep the page content roomy beside the
        // ~210 DIP navigation pane (the seven schedule-day checkboxes need a
        // full-width row). Height holds at 900: the expanders collapse the
        // advanced sections, so the default page is shorter than before.
        SizeToDips(1040, 900);
        EnableMinimumWindowSize();

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
        RefreshNextRun();

        // Initial population fired the dirty handlers; reset so the status
        // reflects the on-disk script.
        _dirty = false;
        _imageDirty = false;
        // Seed both pages' status text without announcing at launch. File Backup
        // is the active page, so refresh it last - its text is what the bar shows
        // and what _lastAnnouncedStatus should match.
        RefreshImageStatus(announce: false);
        RefreshScriptStatus(announce: false);

        // If settings are already saved, surface the backup size and destination
        // space on launch too, not only after a manual save. Silent and
        // off-thread (announce:false), so it never speaks over the opening window
        // or blocks it. The image page checks lazily on first visit instead (see
        // CheckImageAvailability), where its wbadmin probe already runs.
        if (File.Exists(GuardPaths.ScriptPath) && !_dirty)
            StartSpaceStatusCheck(announce: false);

        AppWindow.Closing += OnAppWindowClosing;
    }

    // =====================================================================
    //  PAGE SWITCH (lazy app scan)
    // =====================================================================
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // FileBackupPage / AppMgmtPage are created by InitializeComponent; this
        // can fire during it (NavFile.IsSelected="True"), before the rest of the
        // constructor runs, so guard on the pages existing.
        if (FileBackupPage == null || AppMgmtPage == null || SystemImagePage == null) return;
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
            _fileStatusText = "File backup settings saved. Last updated " +
                File.GetLastWriteTime(GuardPaths.ScriptPath).ToString("yyyy-MM-dd HH:mm") + ".";
        }
        UpdateStatusBar();
        UpdateSaveEnabled();
        // Only re-announce when the message actually changed; otherwise toggling
        // each day checkbox would re-read the status line on top of the box's own
        // checked/unchecked state. Announce only while the bar is showing this
        // text (File Backup active); the bar repaints silently on a page switch.
        if (announce && _activePage == 0 && _fileStatusText != _lastAnnouncedStatus)
            Announce(StatusBarText);
        _lastAnnouncedStatus = _fileStatusText;
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
        else
        {
            // The dot's saved/unsaved colour semantic does not apply to the
            // inventory summary; hide it rather than show a meaningless colour.
            StatusDot.Visibility = Visibility.Collapsed;
            StatusBarText.Text = _appStatusText;
        }
        // Repaint the right-hand progress from the focused page's snapshot.
        RenderStatusBar(_pageProg[_activePage]);
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

    // Per-page progress model. The status bar's right-hand area shows only the
    // FOCUSED page's job (repainted on a page switch, like the left-hand status), and
    // each page's nav ring shows that page's own job - a filling determinate arc once
    // a real percentage is known, spinning while a phase has none. Index by the
    // _activePage convention: 0 File Backup, 1 App Management, 2 System Image. The nav
    // ring is what keeps a job on an unfocused page discoverable; the bar no longer
    // mirrors it, so the two surfaces don't duplicate each other.
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
    private readonly PageProgress[] _pageProg = { new(), new(), new() };

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
    }

    // =====================================================================
    //  DIALOG HELPERS
    // =====================================================================
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

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

        // Two independent reasons to pause a close: unsaved File Backup settings,
        // and a backup/reinstall still running. If neither applies, let the close
        // proceed untouched; otherwise cancel it and drive the close ourselves
        // after the prompts (the second Close() re-enters this handler with
        // _allowClose set, so it sails straight through).
        bool busy = _backupRunning || _reinstalling || _imageRunning;
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

        if (busy)
        {
            // The elevated image cannot be cancelled from this un-elevated
            // process (only wbadmin stop job can), so be honest that it
            // continues; the backup/reinstall trees are killed below.
            string what = _imageRunning
                ? "A system image is still running. It runs with Administrator rights, so it will keep running in the background after GUARD closes."
                : _reinstalling ? "An app reinstall is still running. Closing GUARD stops it."
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
