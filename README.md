# GUARD User Manual

GUARD is a portable backup and app-inventory utility for Windows. It backs up
the folders you choose to any destination you like (an external drive, a second
disk, or a network share), on a schedule if you want one, and it keeps a list of
your installed applications so they are easy to reinstall after a clean Windows
install.

GUARD is built **screen-reader-first**: it is developed by a screen reader user,
and every control is designed to be read and operated cleanly with NVDA and
other assistive tech. If you have ever fought a backup tool's inaccessible grid
of checkboxes, this app is for you. It works just as well with a mouse.

There is no installer and nothing is written outside GUARD's own folder. You
extract one folder, run the exe inside it, and that is the whole installation.

> GUARD is currently in its 0.x series: it is fully usable, but expect new
> features and occasional changes between releases.

## Contents

- [Getting GUARD](#getting-guard)
- [Your first backup, step by step](#your-first-backup-step-by-step)
- [The File Backup tab in full](#the-file-backup-tab-in-full)
- [The App Inventory tab in full](#the-app-inventory-tab-in-full)
- [Where your settings live (portability)](#where-your-settings-live-portability)
- [Accessibility](#accessibility)
- [Frequently asked questions](#frequently-asked-questions)
- [Troubleshooting](#troubleshooting)
- [For developers](#for-developers)

## Getting GUARD

### Requirements

- Windows 10 version 1809 (build 17763) or later, including Windows 11.
- Nothing else. The .NET runtime and everything GUARD needs are bundled inside
  `GUARD.exe`.
- Optional: **winget** (the Windows Package Manager, preinstalled on modern
  Windows) enables automatic app reinstalls in the App Inventory tab. Without
  it, you can still scan and export your app list.

### Download and first run

1. Go to the [Releases page](https://github.com/PlanetLinux98/guard/releases)
   and download `GUARD.zip` from the latest release.
2. Extract the zip. You get a folder named `GUARD` containing `GUARD.exe` and
   this `README.md`.
3. Move the `GUARD` folder somewhere permanent, for example `C:\Tools\GUARD` or
   a folder in your user profile. GUARD writes its settings next to the exe, so
   give it a home rather than running it from Downloads.
4. Run `GUARD.exe`.

**About the SmartScreen warning:** the first time you run GUARD, Windows
SmartScreen may show "Windows protected your PC" because GUARD is not signed
with a paid code-signing certificate. To proceed, choose **More info**, then
**Run anyway**. This warning is about the unsigned download, not about anything
GUARD does; the source code is public if you want to check for yourself.

There is no installer and no uninstaller because neither is needed: GUARD never
touches the registry for its own settings and never writes outside its folder.
(The one optional exception is the Windows scheduled task it can create for you;
see [Scheduling](#schedule) and the [FAQ](#frequently-asked-questions).)

Press **F1** (or the **Help** button at the top right) at any time to open this
manual.

## Your first backup, step by step

This walkthrough backs up your Documents folder to an external drive. The same
steps work for any folders and any destination.

1. Start GUARD. It opens on the **File Backup** tab.
2. In **Backup destination**, type the folder your backups should go to, for
   example `E:\Backups`, or click **Browse...** to pick it. Click **Test** to
   confirm GUARD can reach it; Test creates the folder if it does not exist yet.
3. Click **Add Folder...**. In the dialog, set **Source folder** to
   `C:\Users\<you>\Documents` (or use its **Browse...** button) and set
   **Destination subfolder** to a name like `Documents`; that is the folder
   created under your destination to hold this source's files. Click **OK**.
4. Your folder appears in the **Folders to back up** list with its box ticked,
   meaning it is included in the backup.
5. Leave the **Mode** on **Additive** for now (it never deletes anything at the
   destination; see [Mode](#mode) for the full story).
6. Click **Preview**. GUARD runs a dry run and shows in the **Output** box what
   the backup *would* copy, without changing anything. This is a safe way to
   check your setup.
7. Happy with the preview? Click **Run Now**. The progress bar and Output box
   track the backup; when it finishes, your files are in
   `E:\Backups\Documents`.
8. Optionally, set up a schedule: tick **Run a scheduled backup**, tick the days
   you want, pick a time, and click **Save Settings**. Windows will now run the
   backup automatically at those times, even when GUARD is not open.

That is a working backup. The rest of this manual covers every control in
detail.

## The File Backup tab in full

The controls below are described in the order they appear on the tab.

### Backup destination

The root folder all your backups are copied into. It can be on a local drive,
an external drive, or a network share (a path like `\\server\share\Backups`
works). Each folder you back up gets its own subfolder under this root.

- **Browse...** opens a folder picker.
- **Test** checks the destination is reachable, and creates it if it does not
  exist yet. Use this after plugging in a drive or before trusting a network
  path.

### Folders to back up (tick to include)

The list of source folders GUARD backs up. Each row shows the **Source folder**
(the folder on your PC) and the **Destination subfolder** (the folder name it
is copied into under the backup destination).

Each row is a checkbox:

- **Ticked** means the folder is included in the next backup.
- **Unticked** means it is skipped, but stays in the list. Use this to pause a
  folder without losing its entry; tick it again later to resume.

Below the list:

- **Add Folder...** opens a dialog asking for the **Source folder** (with its
  own **Browse...** button) and the **Destination subfolder** name.
- **Remove Folder** deletes the row you last focused in the list from the list
  entirely, after a confirmation. This is different from unticking: Remove
  forgets the folder; unticking just skips it. Removing a folder does not
  delete any files, either at the source or at the destination.

To remove a folder with the keyboard: Tab into the list, arrow to the folder,
then press **Remove Folder** (Alt+R).

### Mode

Two ways to copy, with very different behavior when files are deleted at the
source:

- **Additive** copies new and changed files to the destination and **never
  deletes anything there**. If you delete a file from your PC, the backup copy
  stays. Over time the destination accumulates everything you ever backed up.
  This is the safe default.
- **Mirror** makes the destination **match the source exactly**. New and
  changed files are copied, and **files you deleted from the source are also
  deleted from the destination** on the next backup. The result is a clean,
  exact replica, but it means the backup cannot save you from a deletion you
  made before the last run; once a Mirror backup runs, the destination copy of
  a deleted file is gone too.

If you are unsure, use Additive. Choose Mirror when you want the destination to
be a tidy, exact copy and you understand that deletions propagate.

### Exclude folder names / Exclude file names

Two boxes, one name or pattern per line, for things the backup should skip
everywhere it finds them:

- **Exclude folder names (one per line):** any folder whose name matches is
  skipped along with everything inside it. Examples: `node_modules`, `temp*`,
  `.git`.
- **Exclude file names (one per line):** any file whose name matches is
  skipped. Examples: `*.iso`, `*.tmp`, `~$*` (Office lock files).

Wildcards work in both boxes: `*` matches any text, `?` matches a single
character. The names match anywhere in the tree, so excluding `cache` skips
every folder named `cache` in every source folder.

### Schedule

Tick **Run a scheduled backup** to have Windows run your backup automatically.
While the box is unticked, the day and time controls are greyed out and no task
is registered.

- **On:** tick the days the backup should run: all seven for daily, one for
  weekly, or any custom mix. At least one day must be ticked to save a
  schedule; if none are, Save Settings will ask you to pick a day or turn the
  schedule off.
- **at** (the time picker): the time of day the backup runs. It follows your
  system's 12- or 24-hour clock format.
- **Next run:** shows when Windows will next run the scheduled backup, or
  "(no scheduled task)" if none is registered.

When you click **Save Settings** with the schedule on, GUARD registers a
Windows scheduled task named **GUARD Backup** (you can see it in Task
Scheduler). Saving with the schedule off removes the task. The task runs the
generated backup script directly, so scheduled backups run whether or not GUARD
itself is open. The PC does need to be on at the scheduled time.

### Action buttons

- **Save Settings** (Alt+S) is the apply button for the whole tab. It writes
  your settings to `backup-settings.ini`, regenerates the `guard-backup.cmd`
  backup script, and registers or removes the scheduled task to match the
  schedule checkbox. Nothing you change takes effect until you save; the
  status line below the buttons tells you when you have unsaved changes.
- **Run Now** (Alt+N) saves your settings and runs the backup immediately.
  Progress appears in the bar and the Output box below.
- **Preview** (Alt+P) saves your settings and does a dry run: the Output box
  shows what the backup *would* copy or delete, but nothing is changed. Always
  preview after switching to Mirror or changing excludes.
- **Open Last Log** (Alt+L) opens the log of the most recent backup
  (`Logs\backup_last.log`), including scheduled runs.
- **Open Destination** (Alt+O) opens the backup destination folder in File
  Explorer so you can check the results.

### Status, progress, and output

- The **status line** under the buttons tells you whether your settings are
  saved or whether you have unsaved changes. Screen readers announce changes to
  it automatically.
- The **progress bar** tracks the backup folder by folder.
- The **Output** box shows the live output of the running backup or preview.
  The same information, in more detail, is written to the log.

## The App Inventory tab in full

The App Inventory answers "what is installed on this PC, and how do I get it
all back after a reinstall?" GUARD reads your installed applications from the
Windows uninstall registry, then cross-checks them against **winget** (the
Windows Package Manager) when it is available.

Each app is marked with a **Source**:

- **Winget** means winget knows the app and can reinstall it automatically.
- **Manual** means it must be reinstalled by hand (from its own installer or
  website). It still appears in your exported list so you do not forget it.

The first time you open the tab, GUARD scans automatically.

### Controls, in order

- **List destination** is the folder your exported app list is written to, with
  **Browse...** and **Test** buttons that work exactly like the backup
  destination's. A good choice is a folder on your backup drive.
- **Refresh List** (Alt+R) rescans installed apps, for example after you
  install or remove something.
- **Select All** / **Select None** tick or untick every app currently visible
  in the list (so with a filter active, they only affect the filtered rows).
- **Filter** (Alt+F) narrows the list as you type. It matches the app name,
  publisher, type (try typing `winget`, `store`, or `manual`), or winget
  package ID.
- The **app list** itself: each row is a checkbox showing the application name,
  version, and source. Ticked apps are the ones included when you export or
  reinstall.
- **Export List** (Alt+E) writes the ticked apps to `app-list.json` in the list
  destination. The file is plain, human-readable JSON recording each app's
  name, version, publisher, and (where known) winget package ID, so you can
  read it in any text editor or even run `winget install --id <id>` yourself.
- **Import List** (Alt+I) loads a previously exported `app-list.json` back into
  the list. This is how you bring your app list onto a freshly reinstalled PC.
- **Reinstall Selected** (Alt+S) installs the ticked **Winget** apps one at a
  time via winget, with progress and output shown below. Ticked **Manual**
  apps are skipped (GUARD tells you how many) and must be installed by hand.
- **Open Folder** (Alt+P) opens the list destination in File Explorer.

### The reinstall workflow

After a clean Windows install:

1. Download and extract GUARD (or run it from your backup drive; it is
   portable).
2. On the App Inventory tab, click **Import List** and pick the
   `app-list.json` you exported before the reinstall.
3. Review the list, untick anything you no longer want, and click
   **Reinstall Selected**.

**Administrator rights:** most app installers need admin rights to install
machine-wide. If reinstalls fail with an access or permission error, close
GUARD and run `GUARD.exe` as Administrator (right-click it, "Run as
administrator"), then repeat the import and reinstall.

**Note on portable apps:** apps that run from a folder without an installer do
not appear in the Windows registry, so GUARD cannot detect them. Keep those in
a backed-up folder instead.

## Where your settings live (portability)

Everything GUARD knows lives in its own folder, next to `GUARD.exe`:

| File | What it is |
|---|---|
| `backup-settings.ini` | All your settings: destination, folder list, mode, excludes, schedule. A plain text file. |
| `guard-backup.cmd` | The generated backup script (see below). Regenerated on every Save Settings. |
| `Logs\backup_last.log` | The log of the most recent backup or preview run. |
| `README.md` | This manual; the Help button (F1) opens it. |

Because everything is in one folder, **moving the folder moves the app and its
settings together**: copy the `GUARD` folder to a new PC or a USB stick and
your configuration comes along. (The scheduled task is the one thing tied to
the original PC; after moving, open GUARD and click **Save Settings** once to
register the task on the new machine, and remember the task on the old machine
points at the old path.)

`guard-backup.cmd` deserves a special mention: it is a standalone script built
from your settings. The scheduled task runs it, but you can also double-click
it yourself to run a backup without opening GUARD at all (it prints what it is
doing and pauses at the end so you can read the result). Do not edit it by
hand; GUARD overwrites it on every save. Under the hood it uses **Robocopy**,
the robust file-copy tool built into Windows, which is why GUARD's backups need
no third-party copy engine.

The exported `app-list.json` is written wherever you set the App Inventory's
list destination, which can be anywhere you like.

## Accessibility

GUARD exists because its developer, a screen reader user, wanted a backup tool
that reads properly. Accessibility is not a checklist item here; it is the
design.

What that means in practice:

- **The folder and app lists are real checkboxes**, not a grid or list view.
  A screen reader announces each row's own checked or unchecked state directly,
  with none of the "selected but is it checked?" confusion grids cause.
- **Tab moves between controls; each list is a single Tab stop.** Tab enters a
  list once and the next Tab leaves it, instead of forcing you through every
  row.
- **Arrow keys move between rows inside a list**, and **Space toggles** the
  focused checkbox. The Mode radio buttons follow the same convention: arrows
  move and select in one step.
- **Focus is remembered.** Tabbing back into a list returns you to the row you
  were last on, not the top.
- **Accessible names match visible labels.** What you see on a button is what
  your screen reader says and what voice-control software like Dragon can
  target. Extra hints live in hover tooltips, where they do not clutter the
  spoken name.
- **Status lines are polite live regions.** Changes to the settings status and
  scan results are announced once, without spamming repeats.
- **Alt access keys** reach the important controls directly (for example Alt+D
  for the destination, Alt+A for Add Folder, Alt+S for Save Settings), and
  **F1** opens this help from anywhere in the app.
- **Dark and light Mica theming** follows your Windows setting automatically,
  including high-contrast-friendly system colors.

If something reads badly with your screen reader, that is a bug; please report
it on the [issues page](https://github.com/PlanetLinux98/guard/issues).

## Frequently asked questions

**Is my data sent anywhere?**
No. GUARD has no telemetry, no account, and no cloud component. Backups go only
to the destination folder you choose, and the app list is written only to the
folder you choose. The only network activity GUARD can cause is winget
contacting its own package sources during a reinstall, which is winget's normal
behavior.

**What happens if my destination drive is unplugged at backup time?**
The backup safely does nothing. The script checks the destination first and, if
it is unreachable, logs an error and stops without copying or deleting
anything. Plug the drive back in and run the backup again (or wait for the next
scheduled run).

**Does Mirror delete my files?**
Mirror never touches your source files: the folders on your PC are only ever
read. What Mirror does is make the *destination* match the source, so a file
you deleted from your PC is also deleted from the backup on the next run. That
keeps the backup tidy, but it means Mirror cannot restore something you deleted
before the last backup ran. If you want the backup to keep everything forever,
use Additive. When in doubt, click **Preview** first; it shows any deletions
Mirror would make before anything happens.

**Can I edit guard-backup.cmd to tweak the backup?**
No; it is generated from your settings and overwritten every time you click
Save Settings, so hand edits are lost. Change the settings in GUARD instead.

**How do I uninstall GUARD?**
Two steps. First remove the scheduled task, if you created one: untick **Run a
scheduled backup** and click **Save Settings** (or delete the task named
"GUARD Backup" in Windows Task Scheduler). Then delete the `GUARD` folder.
That is everything; GUARD stores nothing elsewhere. Your backups at the
destination are yours and are not touched.

**Why does reinstalling apps need Administrator rights?**
Most Windows applications install into protected locations like
`C:\Program Files`, which requires elevation. GUARD itself does not need admin
rights for anything else; only the reinstall step inherits this requirement
from the installers it runs. Run GUARD as Administrator just for that step.

**Does GUARD back up open or locked files?**
Files locked exclusively by a running program (such as a live Outlook data
file) can fail to copy; the log will show which. Closing the program and
re-running the backup picks them up.

**Can I back up to a network share?**
Yes. Enter the UNC path (like `\\server\share\Backups`) or a mapped drive
letter as the destination and use **Test** to confirm it is reachable. For
scheduled backups, prefer the UNC path; mapped drive letters may not exist for
the scheduled task's session.

## Troubleshooting

**SmartScreen blocks the app on first run.** Choose **More info**, then **Run
anyway**. See [Download and first run](#download-and-first-run).

**"No settings saved yet" status, or Run Now complains.** Enter a backup
destination and click **Save Settings** once; Run Now and Preview both need a
saved configuration (they save for you, but the destination must be filled in).

**Save Settings asks me to pick a day.** The schedule checkbox is on but no
weekday is ticked. Tick at least one day, or untick **Run a scheduled backup**.

**The scheduled backup did not run.** Check that the PC was on at the scheduled
time, that **Next run** shows a real time after saving, and look in Task
Scheduler for a task named **GUARD Backup**. If you moved the GUARD folder,
click **Save Settings** again so the task points at the new location. The log
(**Open Last Log**) shows whether a run happened and what it did.

**Some folders report errors in the log.** Common causes: files locked by a
running program, or paths needing permissions your account lacks. The log names
each failing file. Junction points such as the hidden "My Music" links inside
Documents are skipped automatically and are not errors.

**winget is reported as not installed.** The App Inventory still works for
scanning and exporting. To get automatic reinstalls, install "App Installer"
from the Microsoft Store, which provides winget on Windows 10 and 11.

**Reinstalls fail with an access error.** Run GUARD as Administrator and try
again; see the admin note in [The reinstall workflow](#the-reinstall-workflow).

**Something reads wrong with my screen reader.** Please
[open an issue](https://github.com/PlanetLinux98/guard/issues); screen reader
bugs are treated as first-class bugs here.

## For developers

GUARD is an SDK-style C# project targeting .NET 10 and Windows App SDK 1.8
(WinUI 3), shipped unpackaged and self-contained.

- **Dev build:** `dotnet build -r win-x64 -c Debug` with the .NET 10 SDK.
- **Shipping build:** `publish-singlefile.cmd` produces the single-file
  `GUARD.exe` (self-contained, compressed, ReadyToRun), stages it in a `GUARD\`
  folder with this README, and zips it to `GUARD.zip`, the release asset.
- **NativeAOT** is not currently usable: `publish-aot.cmd` links a native
  binary, but it crashes at startup when a data-templated list renders, a
  known WinUI-3-under-AOT limitation (microsoft/WindowsAppSDK discussion
  #3856). The shipping build is ReadyToRun.
- **Contributing:** the repo is trunk-based; work happens on short-lived
  branches off `main` with a PR per change, and every change updates
  [CHANGELOG.md](CHANGELOG.md). Issues and PRs are welcome, especially
  accessibility reports.

GUARD is released under the MIT License; see [LICENSE](LICENSE).

---

*GUARD is developed by [PlanetLinux98](https://github.com/PlanetLinux98/guard).*
