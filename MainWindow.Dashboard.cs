using System;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GuardWui3;

// Protection Status page: whether the user is actually protected right now,
// across File Backup, System Image and App Management.
//
// It answers from artifacts, never from settings alone - the run logs, what is
// at each destination, and which exports exist - because "configured" and
// "protected" come apart in exactly the cases that matter (a schedule that
// never fired, a backup drive that was reformatted, an export on a stick that
// is long gone).
public sealed partial class MainWindow : Window
{
    // Seeded rather than left blank: the probe below yields, and the nav's own
    // page switch paints the status bar before it lands - which showed an empty
    // bar (and no answer to the read-status-bar hotkey) for as long as a dead
    // share took to time out.
    private string _dashStatusText = "Checking your protection...";
    private Brush? _dashStatusBrush;
    private int _dashSeq;
    // Armed by ApplyStartupPage when GUARD opens on this page, spent by the first
    // refresh that lands.
    private bool _announceDashOnLaunch;
    private bool _dashBusy;

    private void OnDashRefresh(object sender, RoutedEventArgs e)
    {
        // Deliberately not IsEnabled=false: Check Again is the only control in
        // its bar, so disabling it while it holds focus lets WinUI throw focus at
        // an arbitrary neighbour, and a screen reader cancels what it is speaking
        // on a focus event. Say what is happening instead, and do not stack a
        // second walk of every source tree behind the first.
        if (_dashBusy) { AnnounceNotification("Still checking your protection..."); return; }
        RefreshDashboard(announce: true);
    }

    // A visible sign the check is running, which only a screen reader had before:
    // the spoken line said "Checking your protection..." while the window showed
    // nothing at all, and on a dead share the walk can run for seconds. Page 4
    // already owns a slot in the shared progress machinery for the status bar,
    // and painting it moves no focus.
    private void SetDashBusy(bool busy)
    {
        _dashBusy = busy;
        Progress(4, p =>
        {
            p.Running = busy;
            p.Indeterminate = busy;
            // Cleared rather than left as an outcome (the convention for the job
            // pages): the verdict already IS the status bar's own line here, so
            // keeping it in the progress area would say it twice.
            p.Text = busy ? "Checking your protection..." : "";
            p.AreaVisible = busy;
            p.BarVisible = busy;
        });
    }

    private void OnDashGoFile(object sender, RoutedEventArgs e) => GoToPage(NavFile);
    private void OnDashGoImage(object sender, RoutedEventArgs e) => GoToPage(NavImage);
    private void OnDashGoApps(object sender, RoutedEventArgs e) => GoToPage(NavApps);

    // Focused as well as selected, so a screen reader follows the jump; the
    // Ctrl+number accelerators do the same.
    private void GoToPage(NavigationViewItem item)
    {
        Nav.SelectedItem = item;
        item.Focus(FocusState.Programmatic);
    }

    // Repaints the whole page. The disk work runs off the UI thread (an app-list
    // destination on a dead share can make one Directory.Exists block for
    // seconds); the verdicts are then computed here, on the UI thread, so the
    // live configuration is never enumerated from a worker.
    private async void RefreshDashboard(bool announce)
    {
        if (DashOverallText == null) return;
        int seq = ++_dashSeq;
        SetDashBusy(true);
        try
        {
            // Whether the destination still holds anything is answered by the
            // source-health walk, not by the probe below, and that walk otherwise
            // only runs at launch and around a save or a run - so this page, and
            // Check Again with it, reported whatever was left over. Started before
            // the probe and awaited after it, so the two walks overlap. Only once
            // something is saved: before that there is nothing to compare against.
            var health = System.IO.File.Exists(GuardPaths.ScriptPath)
                ? RefreshSourceHealthAsync()
                : System.Threading.Tasks.Task.CompletedTask;
            // That walk is capped in seconds, and a button whose whole job is to
            // re-check and tell you must not go quiet while it runs.
            if (announce) AnnounceNotification("Checking your protection...");

            string appDest = Environment.ExpandEnvironmentVariables((_cfg.AppListDest ?? "").Trim());
            var probe = await System.Threading.Tasks.Task.Run(() =>
            {
                var backup = BackupHealth.ReadLog(GuardPaths.LogPath);
                var image = BackupHealth.ReadLog(GuardPaths.SystemImageLogPath);
                bool appReachable = false;
                try { appReachable = appDest.Length > 0 && System.IO.Directory.Exists(appDest); }
                catch { }
                var newest = appReachable ? ProtectionStatus.FindNewestExport(appDest) : null;
                bool imageSaved = System.IO.File.Exists(GuardPaths.SystemImageScriptPath);
                bool backupSaved = System.IO.File.Exists(GuardPaths.ScriptPath);
                return (backup, image, appReachable, newest, imageSaved, backupSaved);
            });
            await health;
            // A newer refresh (or a page switch that started one) has already
            // repainted; dropping this result keeps the older answer off the screen.
            if (seq != _dashSeq) return;

            var now = DateTime.Now;
            PillarStatus file;
            if (!probe.backupSaved)
                file = new PillarStatus(ProtectionLevel.NotSetUp,
                    "No backup settings saved yet; nothing is being backed up.",
                    "Choose a destination and the folders to protect on the File Backup page, then click"
                    + " Save Settings.");
            else
                file = ProtectionStatus.FileBackup(_cfg, probe.backup, now,
                    _sourceHealth.Destination, VanishedToReport.Count, MirrorPurges, MirrorHeld);
            var image = ProtectionStatus.SystemImage(_cfg, _imageAvailable, probe.imageSaved,
                ImageTaskState, probe.image, now);
            var apps = ProtectionStatus.AppList(_cfg.AppListDest, probe.appReachable, probe.newest, now);

            Paint(DashFileDot, DashFileText, DashFileDetail, file);
            Paint(DashImageDot, DashImageText, DashImageDetail, image);
            Paint(DashAppsDot, DashAppsText, DashAppsDetail, apps);

            var overall = ProtectionStatus.Overall(file, image, apps);
            DashOverallDot.Fill = new SolidColorBrush(
                overall == ProtectionLevel.Protected ? StatusGreen : StatusAmber);
            DashOverallText.Text = ProtectionStatus.OverallHeadline(overall);
            DashOverallDetail.Text = "GUARD checks this from your backup logs and destinations, not from"
                + " your settings, so a backup that stopped running or a destination that was wiped shows"
                + " up here.";

            // The bar carries the same one-line verdict, so the read-status-bar
            // hotkey answers the page's own question, and it is the single place
            // this is spoken: three card lines announcing themselves would talk over
            // each other and over the nav's page announcement.
            _dashStatusBrush = new SolidColorBrush(
                overall == ProtectionLevel.Protected ? StatusGreen : StatusAmber);
            _dashStatusText = ProtectionStatus.OverallHeadline(overall);
            // Always false here, and the announcement raised explicitly below: the
            // live region suppresses text identical to what it last said, so
            // "Check Again" - a button whose entire job is to re-check and tell you
            // - was silent in its commonest outcome, nothing has changed. A user
            // could not tell that from the button doing nothing at all.
            CommitPageStatus(4, announce: false);
            if (announce) AnnounceNotification(_dashStatusText);
            // Opening here, nothing else says how protected you are: the nav speaks
            // the page name when focus lands on it and this check finishes after
            // that. Settled by two seconds like the scan summary, or the focus
            // announcement cuts it off, and dropped if a launch dialog is up by then.
            else if (_announceDashOnLaunch)
            {
                _announceDashOnLaunch = false;
                AnnounceSettled(_dashStatusText, 2000, notWhileDialog: true);
            }
        }
        // Only the newest refresh clears the busy state. A superseded one
        // returning early must leave it alone, or the walk still running would
        // show as finished.
        finally { if (seq == _dashSeq) SetDashBusy(false); }
    }

    // Colour reinforces the level but never carries it alone; the text always
    // says it in words (the status bar's own rule).
    private void Paint(Microsoft.UI.Xaml.Shapes.Ellipse dot, TextBlock text, TextBlock detail,
        PillarStatus status)
    {
        // Something this PC cannot do gets no dot at all. It is not green (it is
        // not protecting anything) and not amber (there is nothing to fix), and
        // an amber dot here contradicted the overall verdict, which deliberately
        // ignores it - so a Home PC read "You are protected" beside a warning.
        dot.Visibility = status.Level == ProtectionLevel.Unavailable
            ? Visibility.Collapsed : Visibility.Visible;
        dot.Fill = new SolidColorBrush(
            status.Level == ProtectionLevel.Protected ? StatusGreen : StatusAmber);
        text.Text = status.Headline;
        detail.Text = status.Detail;
    }
}
