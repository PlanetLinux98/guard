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
    private Process? _runningProc;
    private Process? _reinstallProc;

    private bool _dirty;
    private int _progTotal;

    private bool _appScanned;
    private bool _scanning;
    private bool _reinstalling;
    private bool _wingetAvailable;
    private bool _allowClose;

    private FolderPair? _currentFolder;
    private FrameworkElement? _lastFolderFocus;
    private FrameworkElement? _lastAppFocus;
    private readonly List<AppEntry> _allApps = new();
    private string _appFilter = "";

    // Bound by x:Bind in the XAML.
    public ObservableCollection<FolderPair> Folders => _cfg.Folders;
    public ObservableCollection<AppEntry> AppRows { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "GUARD";
        SizeToDips(820, 900);

        _cfg = SettingsStore.Load();

        // Populate the file-tab inputs from settings.
        TxtDest.Text = _cfg.Dest;
        RbMirror.IsChecked = _cfg.Mode == "Mirror";
        RbAdditive.IsChecked = _cfg.Mode != "Mirror";
        TxtExDirs.Text = _cfg.ExcludeDirs;
        TxtExFiles.Text = _cfg.ExcludeFiles;
        ChkSchedule.IsChecked = _cfg.ScheduleEnabled;
        TxtTime.Text = _cfg.ScheduleTime;
        TxtAppDest.Text = _cfg.AppListDest;

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

        WireFolderDirty();
        RefreshNextRun();

        // Initial population fired the dirty handlers; reset so the status
        // reflects the on-disk script.
        _dirty = false;
        RefreshScriptStatus();

        AppWindow.Closing += OnAppWindowClosing;
    }

    // =====================================================================
    //  TAB SWITCH (lazy app scan)
    // =====================================================================
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedIndex == 1 && !_appScanned) { _appScanned = true; ScanApps(); }
    }

    // =====================================================================
    //  DIRTY TRACKING + STATUS
    // =====================================================================
    private void OnDirtyChanged(object sender, TextChangedEventArgs e) { _dirty = true; RefreshScriptStatus(); }
    private void OnDirtyChecked(object sender, RoutedEventArgs e) { _dirty = true; RefreshScriptStatus(); }

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

    private void RefreshScriptStatus()
    {
        if (ScriptDot == null || ScriptStatusText == null) return;
        var green = Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50);
        var amber = Color.FromArgb(0xFF, 0xD2, 0x99, 0x22);
        if (!File.Exists(GuardPaths.ScriptPath))
        {
            ScriptDot.Fill = new SolidColorBrush(amber);
            ScriptStatusText.Text = "No settings saved yet. Click Save Settings before running a backup.";
        }
        else if (_dirty)
        {
            ScriptDot.Fill = new SolidColorBrush(amber);
            ScriptStatusText.Text = "You have unsaved changes. Click Save Settings to apply them.";
        }
        else
        {
            ScriptDot.Fill = new SolidColorBrush(green);
            ScriptStatusText.Text = "Settings saved. Last updated " +
                File.GetLastWriteTime(GuardPaths.ScriptPath).ToString("yyyy-MM-dd HH:mm") + ".";
        }
        Announce(ScriptStatusText);
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
        _cfg.ExcludeDirs = TxtExDirs.Text;
        _cfg.ExcludeFiles = TxtExFiles.Text;
        _cfg.ScheduleEnabled = ChkSchedule.IsChecked == true;
        _cfg.ScheduleTime = NormalizeTime(TxtTime.Text, _cfg.ScheduleTime);
        _cfg.AppListDest = (TxtAppDest.Text ?? "").Trim();
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
        SettingsStore.Save(_cfg);
        BackupScript.Write(_cfg);
        if (_cfg.ScheduleEnabled) ScheduledTasks.UpdateFileTask(_cfg);
        else ScheduledTasks.RemoveTask(GuardPaths.FileTaskName);
        RefreshNextRun();
        _dirty = false;
        RefreshScriptStatus();
        return true;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (await SaveAllAsync())
            await ShowMessageAsync("GUARD", "Settings saved. The backup script and scheduled task have been updated.");
    }

    // =====================================================================
    //  SCHEDULE
    // =====================================================================
    private async void OnCreateTask(object sender, RoutedEventArgs e)
    {
        if (!await SaveAllAsync()) return;
        var err = ScheduledTasks.UpdateFileTask(_cfg);
        if (err != null) await ShowMessageAsync("GUARD", "Create/Update daily task reported a problem:\n\n" + err);
        RefreshNextRun();
    }

    private async void OnRemoveTask(object sender, RoutedEventArgs e)
    {
        ScheduledTasks.RemoveTask(GuardPaths.FileTaskName);
        RefreshNextRun();
        await ShowMessageAsync("GUARD", "Removed scheduled task: " + GuardPaths.FileTaskName);
    }

    private void RefreshNextRun()
    {
        if (LblNextRun == null) return;
        var next = ScheduledTasks.QueryNextRun(GuardPaths.FileTaskName);
        LblNextRun.Text = next == null ? "Next run: (no scheduled task)" : "Next run: " + next;
    }

    private static string NormalizeTime(string? text, string fallback)
    {
        text = (text ?? "").Trim();
        if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t.ToString("HH:mm");
        if (DateTime.TryParseExact(text, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            return t.ToString("HH:mm");
        return fallback;
    }

    // =====================================================================
    //  FOLDER ADD / REMOVE
    // =====================================================================
    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.FolderDialog { XamlRoot = Content.XamlRoot, WindowHandle = WindowHandle };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            _cfg.Folders.Add(new FolderPair(true, dlg.SourcePath, dlg.SubFolder));
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        var f = _currentFolder;
        if (f == null)
        {
            await ShowMessageAsync("GUARD", "Tab into the folder list and arrow to the folder you want to remove, then press Remove Folder.");
            return;
        }
        if (await ShowConfirmAsync("GUARD", "Remove this folder from the backup?\n\n" + f.Source))
        {
            _cfg.Folders.Remove(f);
            _currentFolder = null;
        }
    }

    // =====================================================================
    //  APP SCAN
    // =====================================================================
    private void OnRefreshApps(object sender, RoutedEventArgs e) { _appScanned = true; ScanApps(); }

    private void ScanApps()
    {
        if (_scanning) return;
        _scanning = true;
        SetAppBusy(true);
        AppStatus.Text = "Scanning installed apps (this can take a few seconds)...";
        Announce(AppStatus);

        var th = new Thread(() =>
        {
            ScanResult? res = null; string? err = null;
            try { res = AppInventory.DetectApps(); }
            catch (Exception ex) { err = ex.Message; }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (err != null) { AppStatus.Text = "Scan failed: " + err; }
                else if (res != null)
                {
                    _wingetAvailable = res.WingetAvailable;
                    _allApps.Clear();
                    _allApps.AddRange(res.Apps);
                    int auto = 0, man = 0;
                    foreach (var a in res.Apps) { if (a.CanAuto) auto++; else man++; }
                    if (_wingetAvailable)
                        AppStatus.Text = res.Apps.Count + " apps found. " + auto + " reinstallable via winget, " + man + " manual.";
                    else
                        AppStatus.Text = res.Apps.Count + " apps found. winget is not installed, so apps cannot be reinstalled automatically. You can still export the list for reference.";
                    ApplyFilter();
                }
                _scanning = false; SetAppBusy(false);
                Announce(AppStatus);
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
        BtnAppReinstall.IsEnabled = e;
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
            if (f.Length == 0 ||
                (a.Name != null && a.Name.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) >= 0))
                AppRows.Add(a);
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var a in AppRows) a.Include = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var a in AppRows) a.Include = false; }

    // =====================================================================
    //  EXPORT / IMPORT
    // =====================================================================
    private async void OnExportApps(object sender, RoutedEventArgs e)
    {
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

        string path = Path.Combine(_cfg.AppListDest, GuardPaths.AppListFileName);
        try
        {
            AppListIo.Write(path, file);
            SettingsStore.Save(_cfg);
            await ShowMessageAsync("GUARD", "Exported " + picked.Count + " apps to:\n" + path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "Could not write the app list:\n" + path + "\n\n" + ex.Message);
        }
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

        _allApps.Clear();
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
            _allApps.Add(a);
        }
        _appScanned = true;
        ApplyFilter();
        string mac = f.Machine ?? "?";
        string exp = f.Exported ?? "";
        AppStatus.Text = "Imported " + f.Apps.Length + " apps from " + mac +
            (exp.Length > 0 ? " (" + exp + ")" : "") + ". " + auto + " reinstallable, " + man + " manual.";
        Announce(AppStatus);
    }

    // =====================================================================
    //  REINSTALL
    // =====================================================================
    private async void OnReinstall(object sender, RoutedEventArgs e)
    {
        if (_reinstalling)
        {
            await ShowMessageAsync("GUARD", "A reinstall is already running. Wait for it to finish.");
            return;
        }
        var targets = new List<AppEntry>();
        int manual = 0;
        foreach (var a in _allApps)
        {
            if (!a.Include) continue;
            if (a.CanAuto && !string.IsNullOrEmpty(a.Id)) targets.Add(a);
            else manual++;
        }
        if (targets.Count == 0)
        {
            await ShowMessageAsync("GUARD", "None of the ticked apps can be reinstalled automatically.\n\nOnly \"Winget\" apps reinstall automatically; \"Manual\" apps must be reinstalled by hand.");
            return;
        }
        string msg = "Reinstall " + targets.Count + " app(s) via winget, one at a time?";
        if (manual > 0) msg += "\n\n" + manual + " ticked \"Manual\" app(s) will be skipped (install those by hand).";
        msg += "\n\nThis may require Administrator rights.";
        if (!await ShowConfirmAsync("GUARD", msg, "OK", "Cancel")) return;

        _reinstalling = true;
        SetAppBusy(true);
        TxtAppOutput.Text = "";
        SetProgress(AppProgress, AppProgressLabel, targets.Count, 0, "Starting...");

        var th = new Thread(() =>
        {
            int ok = 0, fail = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var app = targets[i];
                int idx = i;
                SetProgress(AppProgress, AppProgressLabel, targets.Count, idx,
                    "Installing: " + app.Name + " (" + (idx + 1) + " of " + targets.Count + ")");
                AppendOut(TxtAppOutput, "\r\n=== Installing " + app.Name + "  [" + app.Id + "] ===\r\n");
                int code;
                try { code = ProcessRunner.RunWingetInstall(app.Id, s => AppendOut(TxtAppOutput, s), p => _reinstallProc = p); }
                catch (Exception ex) { AppendOut(TxtAppOutput, "ERROR: " + ex.Message + "\r\n"); code = -1; }
                if (code == 0) ok++; else fail++;
                SetProgress(AppProgress, AppProgressLabel, targets.Count, idx + 1, "");
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                AppProgressLabel.Text = "Done. " + ok + " installed, " + fail + " failed.";
                AppendOut(TxtAppOutput, "\r\n--- Reinstall complete: " + ok + " installed, " + fail + " failed ---\r\n");
                _reinstalling = false;
                _reinstallProc = null;
                SetAppBusy(false);
            });
        }) { IsBackground = true };
        th.Start();
    }

    // =====================================================================
    //  RUN SCRIPT
    // =====================================================================
    private async void OnRunNow(object sender, RoutedEventArgs e) => await RunScript("");
    private async void OnPreview(object sender, RoutedEventArgs e) => await RunScript("test");

    private async System.Threading.Tasks.Task RunScript(string arg)
    {
        if (_runningProc != null && !_runningProc.HasExited)
        {
            await ShowMessageAsync("GUARD", "A backup is already running. Wait for it to finish.");
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
        _progTotal = 0;
        SetProgress(FileProgress, FileProgressLabel, 1, 0, "");

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
            _runningProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _runningProc.OutputDataReceived += (_, ev) => HandleScriptLine(ev.Data);
            _runningProc.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendOut(TxtOutput, ev.Data + "\r\n"); };
            _runningProc.Exited += (_, _) => AppendOut(TxtOutput, "\r\n--- finished ---\r\n");
            _runningProc.Start();
            _runningProc.BeginOutputReadLine();
            _runningProc.BeginErrorReadLine();
            _runningProc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            AppendOut(TxtOutput, "ERROR launching script: " + ex.Message + "\r\n");
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
                SetProgress(FileProgress, FileProgressLabel, _progTotal > 0 ? _progTotal : 1, _progTotal,
                    "Backup complete (" + _progTotal + " of " + _progTotal + ").");
                return;
            }
            var m = Regex.Match(rest, "^(\\d+)\\s+(\\d+)\\s*(.*)$");
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                int tot = int.Parse(m.Groups[2].Value);
                string nm = m.Groups[3].Value.Trim();
                _progTotal = tot;
                SetProgress(FileProgress, FileProgressLabel, tot, n - 1, "Backing up: " + nm + " (" + n + " of " + tot + ")");
            }
            return;
        }
        AppendOut(TxtOutput, data + "\r\n");
    }

    private void SetProgress(ProgressBar bar, TextBlock lbl, double max, double val, string text)
    {
        if (bar == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (max > 0) bar.Maximum = max;
            bar.Value = val;
            if (lbl != null) lbl.Text = text;
        });
    }

    private void AppendOut(TextBox box, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            box.Text += text;
            box.Select(box.Text.Length, 0);
        });
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
        if (File.Exists(GuardPaths.ReadmePath)) { OpenPath(GuardPaths.ReadmePath); return; }
        try { Process.Start(new ProcessStartInfo(GuardPaths.RepoUrl) { UseShellExecute = true }); }
        catch (Exception ex) { await ShowMessageAsync("GUARD", "Could not open help:\n\n" + ex.Message); }
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.AboutDialog { XamlRoot = Content.XamlRoot };
        await dlg.ShowAsync();
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
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(dipWidth * scale), (int)(dipHeight * scale)));
        }
        catch { }
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
        await dlg.ShowAsync();
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
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    // =====================================================================
    //  CLOSE GUARD
    // =====================================================================
    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        bool busy = (_runningProc != null && !_runningProc.HasExited) || _reinstalling;
        if (!busy) return;

        args.Cancel = true;
        string what = _reinstalling ? "An app reinstall is still running." : "A backup is still running.";
        if (await ShowConfirmAsync("GUARD", what + " Close anyway?"))
        {
            _allowClose = true;
            Close();
        }
    }
}
