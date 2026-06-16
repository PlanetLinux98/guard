# GUARD User Manual

GUARD is a portable and accessible backup and app-management utility for Windows. It backs up
the folders you choose to any destination (an external drive, a second disk, or
a network share), on a schedule if you want one, and it keeps a list of your
installed applications, with their settings, so you can rebuild a PC after a
clean Windows install.

There is no installation needed, and the app is fully portable to move between PC's 
or system reinstalls. You extract one folder and run the exe inside it. GUARD writes 
no program files outside its own folder (the one optional exception is the Windows scheduled
task it can create for you).

GUARD is built to be fully usable with either the keyboard or the mouse, and to
read cleanly with a screen reader. Most controls have an Alt access key, and
the lists can be navigated with the arrow keys or by typing the first letter of
an item.

Press **F1**, or the **Help** button at the top right, to open this manual from
inside the app at any time.

> GUARD is in its 0.x series: it is fully usable, but features and behaviour may
> change between releases.

## Contents

- [Getting GUARD](#getting-guard)
- [Your first backup](#your-first-backup)
- [The File Backup tab](#the-file-backup-tab)
- [The App Management tab](#the-app-management-tab)
- [The status bar](#the-status-bar)
- [Where your files live (portability)](#where-your-files-live-portability)
- [Keyboard and screen-reader notes](#keyboard-and-screen-reader-notes)
- [Frequently asked questions](#frequently-asked-questions)
- [Troubleshooting](#troubleshooting)
- [For developers](#for-developers)

## Getting GUARD

### Requirements

- Windows 10 version 1809 (build 17763) or later, including Windows 11.
- Nothing else to install. The .NET runtime and everything GUARD needs are
  bundled inside `GUARD.exe`.
- Optional: **winget** (the Windows Package Manager, preinstalled on current
  Windows) enables automatic app reinstalls. Without it you can still scan and
  export your app list.

### Install with winget

If you have winget (the Windows Package Manager), the quickest way to get GUARD
is:

```
winget install --id PlanetLinux98.GUARD
```

`winget install GUARD` also works. New releases usually appear in winget within
a couple of days of the GitHub Release, so `winget upgrade` keeps GUARD current.
Once installed, launch it like any app, then move on to
[Your first backup](#your-first-backup).

### Download manually

Prefer a portable copy you control, or do not have winget? Download it instead:

1. On the [Releases page](https://github.com/PlanetLinux98/guard/releases),
   download `GUARD.zip` from the latest release.
2. Extract the zip. You get a folder named `GUARD` containing `GUARD.exe` and
   this manual (`USER_GUIDE.md`).
3. Move the `GUARD` folder somewhere permanent, for example `C:\Tools\GUARD`.
   GUARD writes its settings next to the exe, so give it a home rather than
   running it from your Downloads folder.
4. Run `GUARD.exe`.

The first time you run it, Windows SmartScreen may show "Windows protected your
PC" because GUARD is not signed with a paid code-signing certificate. Choose
**More info**, then **Run anyway**. The source code is public if you want to
check it. (Installing through winget avoids this prompt.)

There is no uninstaller because none is needed: GUARD never writes to the
registry for its own settings and never writes outside its folder. To remove a
manually downloaded copy, turn off any scheduled backup (see
[Schedule](#schedule)) and delete the folder; if you installed through winget,
run `winget uninstall --id PlanetLinux98.GUARD` (turn off the scheduled backup
first).

## Your first backup

This walkthrough backs up your Documents folder to an external drive. The same
steps work for any folders and any destination.

1. Start GUARD. It opens on the **File Backup** tab.
2. In **Backup destination** (Alt+D), type the folder your backups go to, for
   example `E:\Backups`, or use **Browse...** to pick it. Click **Test** to
   confirm GUARD can reach it; Test creates the folder if it does not exist yet.
3. Click **Add Folder...** (Alt+A). Set **Source folder** to your Documents
   folder, set **Destination subfolder** to a name like `Documents` (the folder
   created under your destination to hold this source's files), and click **OK**.
4. The folder appears in **Folders to back up** with its box ticked, meaning it
   is included in the backup.
5. Leave **Mode** on **Additive** for now; it never deletes anything at the
   destination.
6. Click **Preview** (Alt+P). GUARD does a dry run and shows in the **Output**
   box what the backup *would* copy, without changing anything.
7. Happy with the preview? Select **Run Now** (Alt+N). When it finishes, your
   files are in `E:\Backups\Documents` and a summary appears in the output box
   and the status bar.
8. Optional: tick **Run a scheduled backup**, tick the days, pick a time, and
   click **Save Settings** to have Windows run the backup automatically, even
   when GUARD is closed.

The rest of this manual covers every control and function in detail.

## The File Backup tab

The controls are described in the order they appear on the tab.

### Backup destination

The root folder all your backups are copied into. It can be a local drive, an
external drive, or a network share (a UNC path like `\\server\share\Backups`
works). Each folder you back up gets its own subfolder under this root.

- **Browse...** (Alt+B) opens a folder picker.
- **Test** checks the destination is reachable and creates it if it does
  not exist yet. Use it after plugging in a drive or before trusting a network
  path.

### Folders to back up

The list of source folders GUARD backs up. Each row shows the **Source folder**
(the folder on your PC) and the **Destination subfolder** (the folder name it is
copied into under the backup destination). Each row is a checkbox:

- **Ticked** means the folder is included in the next backup.
- **Unticked** means it is skipped but stays in the list, so you can pause a
  folder without losing its entry and tick it again later.

Below the list:

- **Add Folder...** (Alt+A) opens a dialog asking for the **Source folder** (with
  its own Browse button) and the **Destination subfolder** name.
- **Edit Folder...** (Alt+E) changes the source path or destination subfolder of
  an existing pair in place. It edits the row that last had focus / selection.
- **Remove Folder** (Alt+R) deletes the row that last had focus from the list
  entirely, after a confirmation. This is different from unticking: Remove
  forgets the folder; unticking just skips it. Removing a folder never deletes
  any files, at the source or the destination.

To act on a specific row with the keyboard, Tab into the list, arrow to the row,
then use Edit Folder or Remove Folder.

### Exclusions

Things the backup should skip wherever it finds them. Most cases need no typing:
tick any of the four presets, which cover the common clutter:

- **Temporary files** (`*.tmp`, `*.bak`, `~$*` lock files)
- **System clutter** (Thumbs.db, desktop.ini, .DS_Store, $RECYCLE.BIN, System
  Volume Information)
- **Developer folders** (node_modules, .git, bin, obj, .vs)
- **Caches and disc images** (`cache` and `.cache` folders, `*.iso`, `*.img`)

For anything else, select **Add Exclusion...** (Alt+U in the exclusions area). The
dialog asks what you want to exclude:

- **Folders with a certain name** (skips every folder with that name and all its
  contents),
- **Files of a certain type (extension)**, or
- **Files matching a name or pattern**.

GUARD builds the correct pattern for you. Your custom exclusions appear in a list
with **Remove Exclusion** (Alt+X) to delete the one in focus.

If you used an earlier GUARD that had free-text exclude boxes, your saved entries
are migrated automatically on first load: lines a preset covers tick that preset,
and the rest become custom entries. Be sure to review the preset checkboxes 
after upgrading.

### Mode

Two ways to copy, with very different behaviour when files are deleted at the
source:

- **Additive** (Alt+I) copies new and changed files and **never deletes anything
  at the destination**. If you delete a file from your PC, the backup copy stays.
  This is the safe default.
- **Mirror** (Alt+M) makes the destination **match the source exactly**. Files
  you deleted from the source are also deleted from the destination on the next
  backup. The result is a clean replica, but it means the backup cannot save you
  from a deletion made before the last run.

If you are unsure, use Additive. **Preview** shows any deletions Mirror would
make before anything happens.

### Keep dated backup versions

By default each backup copies into the destination subfolders directly, so the
destination holds the latest backup. Tick **Keep dated backup versions** (Alt+V)
to copy each run into a dated folder (`YYYY-MM-DD`) inside the destination
instead, keeping a history. Set **Versions to keep** to how many dated
copies to retain (default 5); after a clean run, older dated folders beyond that
count are pruned (deleted). Pruning only ever removes folders directly under the
destination whose names exactly match the date pattern.

### Schedule

Tick **Run a scheduled backup** (Alt+Y) to have Windows run your backup
automatically. While it is off, the day and time controls are greyed out and no
task is registered.

- **Days:** tick the days the backup runs (all seven for daily, one for weekly,
  or any mix). At least one day must be ticked to save a schedule.
- **Time** (the time picker): the time of day the backup runs, following your
  system's 12- or 24-hour clock.
- **Automatically run when the backup destination becomes available** (Alt+W) is
  a separate option that works with or without the day/time schedule. It quietly
  checks for the destination every 15 minutes and at sign-in, and runs the backup
  at most once a day when the destination is reachable, so plugging in the
  external drive (or the network share coming back) wouldd trigger that day's
  backup within 15 minutes of becoming reachable.
- **Next run** shows when Windows will next run the scheduled backup, or that no
  task is registered.

When you save with the schedule on, GUARD registers a Windows scheduled task
named **GUARD Backup** (and **GUARD On-Connect Backup** for the on-connect
option); you can see them in Task Scheduler. Saving with everything off removes
them. The task runs the generated backup script directly, so scheduled backups
run whether or not GUARD is open. The PC does need to be on at the scheduled time.

### Action buttons

- **Save Settings** (Alt+S) is the apply button for the whole tab. It writes your
  settings to `backup-settings.ini`, regenerates the `guard-backup.cmd` script,
  and registers or removes the scheduled tasks to match your choices. Nothing you
  change takes effect until you save. The save runs in the background and confirms inline in the status bar; it also reports the
  destination's free space, a rough size for a first full backup, and any
  included source folders that are not currently reachable (those are simply
  skipped at run time). A dialog appears if there is a problem saving.
- **Run Now** (Alt+N) saves your settings and runs the backup immediately, with
  progress in the status bar and output box.
- **Preview** (Alt+P) saves your settings and does a dry run: the output box
  shows what the backup *would* copy or delete, but nothing changes. Always
  preview after switching to Mirror or changing exclusions.
- **Stop Backup** (Alt+C) cancels a running backup (the whole script and robocopy
  process tree is stopped). It is available only while a backup is running.
- **Open Last Log** (Alt+L) opens the log of the most recent run
  (`Logs\backup_last.log`), including scheduled runs.
- **Open Destination** (Alt+O) opens the backup destination in File Explorer.

### Progress, output, and the run summary

The progress bar tracks the backup folder by folder, and the **Output** box shows
its live output. When a run finishes, GUARD prints a plain-language summary, built
from Robocopy's own totals: files copied (with size), files skipped because they
were already up to date, failures (called out first, with a pointer to the log),
and any extra files at the destination (noting whether Mirror removed them). The
summary also appears on the progress line and in the status bar, so you rarely
need to read the raw log.

## The App Management tab

This tab answers "what is installed on this PC, and how do I get it all back
after a reinstall?" GUARD reads your installed applications from the Windows
uninstall registry and cross-checks them against winget when it is available. The
tab scans automatically the first time you open it.

Each app shows a **Source**:

- **Winget** means winget can reinstall it automatically.
- **Store** means it came from the Microsoft Store.
- **Manual** means it must be reinstalled by hand (from its own installer or
  website). It still appears in your exported list so you do not forget it.

### Controls

- **List destination** (Alt+D), with **Browse...** (Alt+B) and **Test**,
  is the folder your exports are written to. A folder on your backup drive is a
  good choice.
- **Refresh List** (Alt+R) rescans installed apps, for example after you install
  or remove something.
- **Select All** (Alt+L) / **Select None** (Alt+N) tick or untick every app
  currently visible, so with a filter active they affect only the matching rows.
- **Filter** (Alt+F) narrows the list as you type. It matches the app name,
  publisher, type (try `winget`, `store`, or `manual`), or winget package ID. A
  count beside the box shows how many apps match (for example "42 of 187 apps").
- The **app list**: each row is a checkbox showing the application name, version,
  and source. Ticked apps are the ones included when you export or reinstall.

### Exporting your apps (and their settings)

Click **Export** (Alt+E) to write the ticked apps to a new, dated export folder
(`app-export-YYYY-MM-DD_HHMM`) under the list destination, containing
`app-list.json`. The JSON is plain and human-readable: each app's name, version,
publisher, and (where known) winget package ID, so you can read it in any text
editor or even run `winget install --id <id>` yourself. Each export goes into its
own dated folder, so repeated exports never overwrite each other.

Tick **Also export app settings** (Alt+A) before exporting to bundle the apps'
settings folders alongside the list. GUARD matches the ticked apps against folder
names under `%APPDATA%`, `%LOCALAPPDATA%`, and `%USERPROFILE%\.config`, then shows
the matches in a confirmation dialog of tickable rows (with Select All / Select
None, and folder sizes that calculate in the background while you review).
Confirm, and the chosen folders are copied into an `AppSettings` folder inside the
export, sorted by which root they came from, with a progress bar that advances by
size. A manifest and a plain-text restore note are written alongside.

A few notes on settings export:

- **Cancelling the confirmation cancels the whole export**, so a cancel never
  leaves a half-finished result.
- Files locked by a running program are skipped and counted rather than failing
  the export, so for a complete copy, close the apps whose settings you are
  exporting (browsers and Electron apps especially).
- Not copied: cache subfolders, junctions, registry-stored settings, ProgramData,
  and Store packaged-app state (an OS-managed container a file copy cannot
  restore).

### Importing on a new PC: reinstall and restore

After a clean Windows install, copy or run GUARD from your backup drive, then on
the App Management tab:

1. Click **Import List** (Alt+I) and pick the `app-list.json` from a saved export.
   This opens the Import dialog, which lists the saved apps as tickable rows, with
   the source machine and export date in its header.
2. Untick anything you no longer want, then choose an action:
   - **Reinstall Selected** installs the ticked Winget and Store apps one at a
     time, with progress and output shown on the main tab. Manual apps are skipped
     and counted; install those by hand.
   - **Reinstall & Restore Settings** does the same and then puts the apps'
     settings folders back. This button is available only when the export
     included an `AppSettings` bundle (GUARD finds it automatically as a sibling
     of the imported `app-list.json`).
3. If you chose to restore settings, a second confirmation appears with each
   settings folder as a tickable row, showing its target location (re-anchored to
   your current user profile via the manifest, so it works even if the Windows
   username changed), whether a folder already exists there, and its size. Review
   and confirm.

How the restore is kept safe:

- **Nothing is overwritten silently.** Before replacing an existing target
  folder, GUARD renames the old one to `<name>.guard-old-<timestamp>`, so a
  restore is fully reversible; those renames are counted in the summary.
- **Settings are restored after all the installs finish**, before you launch
  anything, which is the safe moment (apps often write their defaults on first run, and
  winget does not auto-launch them).
- **Folders whose app is currently running are skipped and counted** rather than
  failing.
- If none of the ticked apps reinstall automatically, the settings are restored
  on their own.

Other controls here:

- **Stop Reinstall** (Alt+C) stops the reinstall loop after the current app;
  apps already installed stay installed. It is available only while a reinstall is
  running.
- **Open Folder** (Alt+O) opens the list destination in File Explorer.

Two practical notes:

- **Administrator rights:** most installers need admin rights to install
  machine-wide. If reinstalls fail with an access or permission error, close
  GUARD and run `GUARD.exe` as Administrator (right-click, "Run as
  administrator"), then repeat the import and reinstall.
- **Portable apps** that run from a folder without an installer do not appear in
  the Windows registry, so GUARD cannot detect them. Keep those in a backed-up
  folder instead.

## The status bar

A status bar runs along the bottom of the window. It shows the active tab's
status (the saved/unsaved line on File Backup, the scan or import summary on App
Management) and, while a backup or reinstall is running, a compact progress bar
with the current action, so progress stays visible even when the in-tab progress
area is scrolled away or another tab is active. After a job ends, the bar keeps
that run's outcome until the next job starts.

The status bar is exposed as an actual status bar element, so a
screen reader's read-the-status-bar command reads it on demand, including a
running job's progress text.

## Where your files live (portability)

Everything GUARD knows lives in its own folder, next to `GUARD.exe`:

| File | What it is |
|---|---|
| `backup-settings.ini` | All your settings: destination, folder list, mode, exclusions, schedule. Plain text. |
| `guard-backup.cmd` | The generated backup script. Regenerated on every Save Settings. Theoretically you could run this script portably from anywhere to do an on-demand backup. |
| `Logs\backup_last.log` | The log of the most recent backup or preview run. |
| `USER_GUIDE.md` | This manual; the Help button (F1) opens it. |

Because everything is in one folder, **moving the folder moves the app and its
settings together**: copy the `GUARD` folder to another PC or a USB stick and
your configuration comes along. The scheduled task is the one thing tied to the
original PC; after moving, open GUARD and click **Save Settings** once to register
the task on the new machine.

`guard-backup.cmd` is a standalone script built from your settings. The scheduled
task runs it, but you can also double-click it to run a backup without opening
GUARD; it prints what it is doing and pauses at the end. Do not edit it by hand;
GUARD overwrites it on every save. Under the hood it uses **Robocopy**, the robust
file-copy tool built into Windows.

Exports are written wherever you set the App Management list destination.

## Keyboard and screen-reader notes

GUARD is fully operable from the keyboard and designed to read cleanly with a
screen reader. A few specifics worth knowing:

- **The folder and app lists are checkbox items**, not a grid, so a screen
  reader announces each row's own checked state directly.
- **Tab treats each list as a single stop:** Tab enters the list once and the
  next Tab leaves it. Inside a list, the **arrow keys move between rows**
  and **Space toggles** the focused checkbox.
- **First-letter navigation:** with focus in a list, type a letter to jump to
  the next row that starts with it, and press it again to cycle through the
  matches; typing several letters quickly matches a longer prefix.
- **Focus is remembered:** tabbing back into a list returns you to the row you
  were last on, not the top.
- The **Mode** radio options follow the same  expected convention:
  arrow keys move and select at once.
- **Alt access keys** reach the important controls directly (the access key for
  each control is given in parentheses throughout this manual), and **F1** opens
  this manual from anywhere.
- **Dark and light Mica theming** follows your Windows setting automatically.

If something reads or behaves badly with your screen reader, that is treated as a
serious bug; please report it on the
[issues page](https://github.com/PlanetLinux98/guard/issues).

## Frequently asked questions

**Is my data sent anywhere?**
No. GUARD has no telemetry, no account, and no cloud component. Backups and exports 
go only to the destination you choose. The only internet-reaching network (WAN) 
activity GUARD can cause is winget contacting its official package sources during an app reinstall.

**What happens if my destination drive is unplugged at backup time?**
The backup safely does nothing: the script checks the destination first and, if
it is unreachable, logs an error and stops without copying or deleting anything.
Plug the drive back in and run again, or wait for the next scheduled run. (If you
enabled the on-connect option, plugging the drive in triggers that day's backup.)

**Does Mirror delete my files?**
Mirror never touches your source files; the folders on your PC are only ever read.
It makes the *destination* match the source, so a file you deleted from your PC is
also deleted from the backup on the next run. If you want the backup to keep
everything, use Additive. When in doubt, run a Preview first.

**Can I edit guard-backup.cmd to tweak the backup?**
Not when used with GUARD; it is generated from your settings and overwritten on every
Save Settings, so hand edits are lost. Change the settings in GUARD instead.
If you plan to use the script elsewhere, without GUARD in any way, then you could edit the script before running it.

**How do I "uninstall" GUARD?**
Turn off any scheduled backup (untick the schedule options and Save Settings, or
delete the GUARD tasks in Task Scheduler), then delete the `GUARD` folder. GUARD
stores nothing elsewhere. Your backups at the destination are untouched.

**Why does reinstalling apps need Administrator rights?**
Most applications install into protected locations like `C:\Program Files`, which
requires elevation. GUARD itself does not need admin rights for anything else;
only the reinstall step inherits this from the installers it runs.

**Does GUARD back up open or locked files?**
Files locked exclusively by a running program can fail to copy; the log indicates such. 
Close the program and run the backup again to pick them up. The same
applies to exporting app settings.

**Can I back up to a network share?**
Yes. Enter the UNC path (like `\\server\share\Backups`) or a mapped drive letter, 
or locate the network location via the Browse button, and use Test to confirm it 
is reachable. For scheduled backups, prefer the UNC path; mapped drive letters 
may not exist in the scheduled task's session.

## Troubleshooting

**SmartScreen blocks the app on first run.** Choose **More info**, then **Run
anyway**. See [Download and first run](#download-and-first-run).

**Save Settings asks me to pick a day.** The schedule is on but no weekday is
ticked. Tick at least one day, or turn the schedule off.

**The scheduled backup did not run.** Check that the PC was on at the scheduled
time, that **Next run** shows a  time after saving, and look in Task Scheduler
for **GUARD Backup**. If you moved the GUARD folder, Save Settings again so the
task points at the new location. **Open Last Log** shows whether a run happened.

**Some folders report errors in the log.** Common causes are files locked by a
running program or paths needing permissions your account lacks; the log names
each failing file. Junction points such as the hidden "My Music" links inside
Documents are skipped automatically and are not errors.

**winget is reported as not installed.** Scanning and exporting still work. To get
automatic reinstalls, install "App Installer" from the Microsoft Store, which
provides winget on Windows 10 and 11.

**Reinstalls fail with an access error.** Run GUARD as Administrator and try
again; see the admin note in
[Importing on a new PC](#importing-on-a-new-pc-reinstall-and-restore).

**A settings restore did not change anything for an app.** The app may have been
running (running-app folders are skipped), or its settings live
somewhere GUARD does not copy (the registry, ProgramData, or a Store packaged-app
container). Close the app and restore again, or restore those by hand.

## For developers

GUARD is an SDK-style C# project targeting .NET 10 and Windows App SDK 1.8
(WinUI 3), shipped unpackaged and self-contained. Build, project layout, and
contributing notes are in the [README](README.md). The repo is trunk-based, with
short-lived branches off `main` and a PR per change; accessibility reports are
especially welcome.

GUARD is released under the MIT License; see [LICENSE](LICENSE).

---

*GUARD is developed by [PlanetLinux98](https://github.com/PlanetLinux98/guard).*
