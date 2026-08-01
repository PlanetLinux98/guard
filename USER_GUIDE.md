# GUARD User Manual

GUARD is a portable and accessible backup and app-management utility for Windows. It backs up
the folders you choose to any destination (an external drive, a second disk, or
a network share), on a schedule if you want one, and it keeps a list of your
installed applications, with their settings, so you can rebuild a PC after a
clean Windows install. It can also create full system images of the whole PC, so
you can restore everything (Windows, programs and settings) after a disk failure.

There is no installation needed, and the app is fully portable to move between PCs 
or system reinstalls. You extract one folder and run the exe inside it. GUARD writes 
no program files outside its own folder (the optional exceptions are the Windows
scheduled tasks it can create for you, and a one-time notification registration if
you use notifications).

GUARD is built to be fully usable with either the keyboard or the mouse, and to
read cleanly with a screen reader. Most controls have an Alt access key, and
the lists can be navigated with the arrow keys or by typing the first letter of
an item.

Press **F1**, or the **Help** item at the bottom of the left navigation, to
open this manual from inside the app at any time.

> GUARD is in its 0.x series: it is fully usable, but features and behaviour may
> change between releases.

## Contents

- [Getting GUARD](#getting-guard)
- [Your first backup](#your-first-backup)
- [The File Backup tab](#the-file-backup-tab)
- [The System Image tab](#the-system-image-tab)
- [The App Management tab](#the-app-management-tab)
- [The Settings page](#the-settings-page)
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
  bundled in GUARD's folder.
- Optional: **winget** (the Windows Package Manager, preinstalled on current
  Windows) enables automatic app reinstalls. Without it you can still scan and
  export your app list, and GUARD can install winget for you (see
  [Installing winget](#installing-winget)).

### Install with winget

If you have winget (the Windows Package Manager), the quickest way to get GUARD
is:

```
winget install --id PlanetLinux98.GUARD
```

`winget install GUARD` also works. New releases usually appear in winget within
a couple of days of the GitHub Release, so `winget upgrade` keeps GUARD current.
Once installed, launch GUARD.exe, then move on to
[Your first backup](#your-first-backup).

### Download manually

Prefer a portable copy, or do not have winget? Download it instead:

1. On the [Releases page](https://github.com/PlanetLinux98/guard/releases),
   download `GUARD.zip` from the latest release.
2. Extract the zip. You get a folder named `GUARD` containing `GUARD.exe` and
   this manual (`USER_GUIDE.html`).
3. Move the `GUARD` folder somewhere permanent, for example `C:\Tools\GUARD`.
   GUARD writes its settings next to the exe, so give it a home rather than
   running it from your Downloads folder.
4. Run `GUARD.exe`.

The first time you run it, Windows SmartScreen may show "Windows protected your
PC" because GUARD is not signed with a paid code-signing certificate. Choose
**More info**, then **Run anyway**. The source code is public if you want to
check it. (Installing through winget avoids this prompt.)

However you install it, GUARD keeps itself current: once a day it checks GitHub
for a newer release and offers to install it, and it can update fully
automatically if you prefer (see [The Settings page](#the-settings-page)).

There is no uninstaller because none is needed: GUARD never writes to the
registry for its own settings, and a copy you extracted yourself writes nothing
outside its own folder.

To remove GUARD, first use **Remove GUARD's Scheduled Tasks** on the Settings
page - the schedules are registered with Windows, not stored in GUARD's folder,
so deleting the app without removing them leaves them firing at something that
is no longer there. Then delete the folder, or run
`winget uninstall --id PlanetLinux98.GUARD` if you installed it that way.

A winget install also keeps its settings and logs in `%LOCALAPPDATA%\GUARD`
(see [Where your files live](#where-your-files-live-portability)); winget does
not remove that folder, so delete it too if you want no trace left.

## Your first backup

This walkthrough backs up your Documents folder to an external drive. The same
steps work for any folders and any destination.

1. Start GUARD. It opens on the **File Backup** tab.
2. In **Backup destination** (Alt+D), type the folder your backups go to, for
   example `E:\Backups`, or use **Browse...** to pick it. Click **Test** to
   confirm GUARD can reach it; if the folder does not exist yet, Test offers to
   create it.
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
- **Test** checks the destination is reachable and, if the folder does not
  exist yet, offers to create it. Use it after plugging in a drive or before
  trusting a network path.

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
tick any of the four presets, which cover the common clutter (a fresh install
starts with only System clutter ticked):

- **Temporary files** (`*.tmp`, `*.bak`, `~$*` lock files)
- **System clutter** (Thumbs.db, desktop.ini, .DS_Store, $RECYCLE.BIN, System
  Volume Information)
- **Developer folders** (node_modules, .git, bin, obj, .vs). Note this skips
  *every* folder with those names, wherever it appears; leave it unticked
  unless you know your backed-up folders hold code projects.
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

In Mirror mode, every folder needs its own destination subfolder: folders that
share a subfolder (or nest one inside another) would delete each other's files
on every run, so GUARD refuses to save such a setup.

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
  external drive (or the network share coming back) would trigger that day's
  backup within 15 minutes of becoming reachable.
- **Next run** shows when Windows will next run the scheduled backup, or that no
  task is registered.

When you save with the schedule on, GUARD registers a Windows scheduled task
named **GUARD Backup** (and **GUARD On-Connect Backup** for the on-connect
option); you can see them in Task Scheduler. Saving with everything off removes
them. The tasks run the backup completely invisibly (no window appears, not
even for the 15-minute on-connect check), whether or not GUARD is open. The PC
does need to be on at the scheduled time. To hear about the outcome without
opening GUARD, turn on notifications (see the Settings page): failures notify
by default, successes only if you opt in. The status bar also reports the last
run's outcome the next time you open GUARD.

Six situations GUARD handles for you:

- **Windows moves one of your personal folders.** The seven folders GUARD
  offers by default - Documents, Desktop, Pictures and so on - can be relocated
  by Windows: OneDrive's "Back up your folders" moves several of them under
  your OneDrive folder, and a folder's Properties, Location tab can send one
  anywhere. The old location is usually left behind *empty*, so a backup
  pointed at it would copy nothing and still report success. GUARD tracks which
  Windows folder each of those rows follows, notices when one moves, and offers
  at its next launch to follow it. It never moves on its own - a backup tool
  should not change what it protects without telling you - and if you say no,
  it does not ask again for that move. A folder that moves somewhere else later
  is a new question, so it asks about that one. Editing a row's path yourself
  stops GUARD tracking it: you have said where you want it.
- **A folder you back up goes empty.** If a source folder has nothing left to
  copy *and* your backup still holds files copied from it before, GUARD warns
  you - in the status line, when you save, when a backup runs, and in the
  notification for unattended runs. Both halves matter: a folder that was
  always empty is not a problem and never triggers this. In Mirror mode the
  warning is more urgent, because mirroring makes the backup match the source,
  so the next run would delete those copies too. If you emptied the folder on
  purpose, choose **Don't warn again** when you save; GUARD forgets that
  answer if the folder ever has files in it again, so a real later
  disappearance still gets reported.
- **Your backup itself disappears.** GUARD keeps its own records next to the
  app, not on the backup drive, so a reformatted or emptied destination would
  otherwise still show "Last backup succeeded". If the destination is reachable
  but empty while GUARD's records show a backup has run, it says so and asks you
  to run a backup to rebuild it.

- **Your backup drive changes letters.** If the destination was `E:\Backups`
  and the drive comes back as `F:`, the backup follows it automatically (GUARD
  remembers the drive's volume serial number, not just its letter) and notes
  the change in the log. The next Save Settings updates the saved destination
  to the new letter and tells you.
- **A different drive ends up at the same letter.** If the last-used
  destination letter is reachable but now holds a drive GUARD does not
  recognize (for example, the real backup drive was unplugged and something
  else took `E:`), Save Settings stops and asks you to confirm before
  adopting it; in Mirror mode, adopting the wrong drive blindly could delete
  its files as "extras". Reconnect the real drive, or click Save again if
  the new drive is intentional.
- **You move or rename the GUARD folder.** The scheduled tasks point at
  GUARD's location, so a move would silently break them; GUARD notices at its
  next launch and re-registers the backup tasks against the new location
  automatically. (The scheduled *system image* task needs one Save Settings
  with Administrator approval to repoint; the System Image page tells you when
  that is needed.)

### Action buttons

- **Save Settings** (Alt+S) is the apply button for the whole tab. It writes your
  settings to `backup-settings.ini`, regenerates the `guard-backup.cmd` script,
  and registers or removes the scheduled tasks to match your choices. Nothing you
  change takes effect until you save. The save runs in the background and confirms inline in the status bar; it also reports the
  destination's free space, a rough size for a first full backup, and any
  included source folders that are not currently reachable (those are simply
  skipped at run time). It warns if a path contains a `%` sign that is not an
  environment variable, since Windows command scripts treat `%` specially and
  the backup could misread the path. A dialog appears if there is a problem saving. If you
  close GUARD with changes you have not saved, it asks whether to save them
  first, with Save, Don't Save, and Cancel.
- **Run Now** (Alt+N) saves any unsaved changes, then runs the backup
  immediately, with progress in the status bar and output box.
- **Preview** (Alt+P) likewise saves first, then does a dry run: the output box
  shows what the backup *would* copy or delete, but nothing changes. Always
  preview after switching to Mirror or changing exclusions.
- **Stop Backup** (Alt+T) cancels a running backup (the whole script and robocopy
  process tree is stopped). It is available only while a backup is running.
- **Open Last Log** (Alt+L) opens the log of the most recent real backup
  (`Logs\backup_last.log`), including scheduled runs. Previews keep their own
  separate log, so a dry run never poses as the last real backup.
- **Open Destination** (Alt+O) opens the backup destination in File Explorer.

### Progress, output, and the run summary

The progress bar tracks the backup folder by folder, and the **Output** box shows
its live output. When a run finishes, GUARD prints a plain-language summary, built
from Robocopy's own totals: files copied (with size), files skipped because they
were already up to date, failures (called out first, with a pointer to the log),
mismatches (where Robocopy found a file where the other side now has a
same-named folder, or vice versa, and could not reconcile them), and any extra
files at the destination (noting whether Mirror removed them). The summary also
appears on the progress line and in the status bar, so you rarely need to read
the raw log.

## The System Image tab

A *system image* is a complete copy of the whole PC (Windows, your programs, their
settings and your files) that can be restored onto a blank disk after a failure,
without reinstalling anything. This is different from the File Backup tab, which
copies the folders you choose; an image captures everything needed to boot.

GUARD creates images with the built-in Windows backup engine (`wbadmin`), so there
is nothing extra to install. On Windows editions that do not include that engine
(notably **Windows Home**), the tab says so and the imaging buttons are disabled;
you can still build recovery media there.

> An image is restored from *outside* Windows (you cannot replace the running
> system from within it). You boot from Windows installation media and use its
> built-in System Image Recovery. The Create Recovery Media button builds that
> media for you; see [Restoring from a system image](#restoring-from-a-system-image) below.

### Choosing a destination

Type or **Browse** to a destination. How images are kept depends on the kind of
path you enter, and GUARD shows which applies beneath the box as you type:

- **A local or external disk** (recommended). Give a drive, such as `E:\`. Windows
  keeps several past images on a dedicated disk automatically and removes the
  oldest when space runs low, and this is the most reliable kind of image to
  restore from.
- **A network share.** Give a path such as `\\server\share\Backups`. A share keeps
  **only the most recent image** (each run replaces the last). A scheduled image
  also cannot sign in to a share (see below), so a share is best used with on-demand
  images.

The destination cannot be on the same drive as Windows (an image includes the
Windows drive, so it must be written somewhere separate). GUARD checks the free
space after you save and warns if it looks too small. As on the File Backup
tab, it also warns if the path contains a `%` sign that is not an environment
variable, since Windows command scripts treat `%` specially and the image could
be written to the wrong place.

> **Note:** on a local or external disk, Windows always writes images to the
> drive's root, in a folder named `WindowsImageBackup`. If you type or browse
> to a folder like `E:\Images`, the folder part is ignored; the caption under
> the destination field reminds you when that applies.

### Creating an image

- **Create Image Now** makes an image straight away. Because imaging needs
  Administrator rights, Windows shows one approval prompt; after that you can keep
  using your PC while it runs. Progress and the result appear under **Output
  details** and on the status bar.
- **Stop Image** cancels a running image. Stopping also needs Administrator
  approval (Windows confirms the stop).
- **List Images** (Alt+V), beside the destination field, lists the images
  already stored on that destination, with their dates and what each contains,
  in the Output details area. Windows only answers this question with
  Administrator rights, so each use shows one approval prompt.

### Scheduling

Tick **Create images on a schedule** and pick how often (**Weekly**, **Monthly**
or **Daily**), the day, and the time. Saving registers a Windows scheduled task
that runs as the SYSTEM account with the highest privileges, so the scheduled
image runs quietly with no approval prompt. Registering or changing the schedule
asks for Administrator approval once, at save time.

Full images are large, so Weekly or Monthly usually makes more sense than Daily.
Because a scheduled image runs as SYSTEM, it cannot supply network-share sign-in
details; if you schedule images, store them on a local or external disk.

> **BitLocker:** if your drive is encrypted with BitLocker, keep your BitLocker
> recovery key safe. A restored image is unencrypted until you turn BitLocker back
> on afterwards.

### Create Recovery Media

To restore an image you need to boot the PC from Windows installation media. The
**Create Recovery Media...** button walks you through building a bootable USB
using only built-in tools:

1. GUARD shows the architecture (such as x64 or ARM64) and Windows version the USB
   must match. The edition (Home, Pro, and so on) does not matter, because the USB
   only starts the recovery tools; it does not reinstall Windows.
2. Choose a Windows installation ISO. Use **Get the official ISO from Microsoft**
   to open Microsoft's download page in your browser, then pick the downloaded
   `.iso` file. (Automatic download is not offered: Microsoft requires a manual
   download.)
3. Choose the USB drive. Only removable USB drives are listed, so your internal
   disks are never offered.
4. Confirm. The drive is named back to you and then **completely erased**, so make
   sure it is the right one and that anything important on it is saved elsewhere.
5. GUARD formats the drive, copies the installer, and automatically splits a large
   `install.wim` so it fits, then tells you when the USB is ready. The split is the
   slowest step and can take ten minutes or more on a slow USB drive, during which
   the progress bar does not move; leave the drive in until it finishes.

You only need to build recovery media once; keep the USB with your backup disk.

### Restoring from a system image

This runs outside Windows. The same steps are available in the app from the
**Restore Instructions** button.

1. Connect the disk that holds the image (or make sure the network share is
   reachable).
2. Insert the recovery USB and start the PC from it (use the firmware boot menu,
   often F12, Esc or F9 during startup).
3. At the Windows Setup screen, choose your language, then click **Repair your
   computer** (not Install).
4. Go to **Troubleshoot**, then **Advanced options**, then **System Image
   Recovery**.
5. Pick the latest image (or **Select a system image** to choose a specific one or
   a network location), then follow the prompts to restore.
6. When it finishes, remove the USB and restart. If the drive used BitLocker, turn
   it back on.

**Restoring from a network share.** The recovery environment cannot look up server
names the way Windows does, so when it asks for the network location, type the
share's **IP address** in place of its name (for example `\\10.0.0.50\Backups`
instead of `\\server\Backups`), and enter the share's username and password when
prompted. Give the full path down to the folder you imaged to. To save you looking
the address up, GUARD shows the exact path to type in two places: the **Restore
Instructions** button, and the output box right after an image to a share finishes.

> Restoring works best on the same or very similar hardware. On very different
> hardware Windows may not boot afterwards; in that case do a clean Windows install
> and use the File Backup and App Management tabs to bring back your files and apps.

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
  The source tells you how an app reinstalls: **Winget** apps reinstall
  anywhere winget runs, **Store** apps also need the Microsoft Store on the
  target PC, and **Manual** apps must be reinstalled by hand.
- **Update All Apps** (Alt+U) asks winget to update every app it knows to the
  latest version. It runs with Administrator rights (one approval prompt up
  front) so that apps installed for all users, and Store-delivered packages
  like WSL, can update - a per-app prompt cannot install those. The Output
  details section opens automatically so you can follow winget's progress. It
  is normal for a few apps to report failures (apps installed outside winget,
  or ones that cannot update silently); the summary says so and the output
  lists which. Because it runs elevated it cannot be stopped once it starts,
  so the confirmation says as much; the list rescans itself when the run ends.

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

- **Stop Reinstall** (Alt+T) stops the reinstall loop after the current app;
  apps already installed stay installed. It is available only while a reinstall is
  running.
- **Open Folder** (Alt+O) opens the list destination in File Explorer.

Two practical notes:

- **winget missing on the new PC?** If the ticked apps need winget and it is not
  installed, GUARD offers to install it right there and then carries on with the
  reinstall; see [Installing winget](#installing-winget).
- **Administrator rights:** most installers need admin rights to install
  machine-wide. If reinstalls fail with an access or permission error, close
  GUARD and run `GUARD.exe` as Administrator (right-click, "Run as
  administrator"), then repeat the import and reinstall.
- **Portable apps** that run from a folder without an installer do not appear in
  the Windows registry, so GUARD cannot detect them. Keep those in a backed-up
  folder instead.

### Installing winget

winget (Windows' app package manager, also called App Installer) is preinstalled
on most current Windows PCs, but not on all of them: LTSC and Server editions,
and systems where the Microsoft Store has been removed, ship without it. Those
are exactly the PCs a fresh restore tends to happen on, so GUARD can install
winget for you:

- When the app scan finds winget missing, a notice appears just above the status
  bar with an **Install winget** button; **Ctrl+I** activates it from anywhere
  while the notice is showing. The same button lives on the
  [Settings page](#the-settings-page).
- If you import a list and start a reinstall while winget is missing, GUARD
  offers the install first and, once it succeeds, carries straight on with the
  reinstall.

Whichever way you start it, the same dialog opens and explains what will happen;
choose **Install** (Alt+I) to proceed. GUARD downloads Microsoft's App Installer
package (roughly 300 MB) from the official winget release on GitHub and installs
it for your user account. No Administrator approval is needed, nothing else on
the PC is changed, and Windows itself verifies the package's Microsoft signature
during installation. The dialog shows download progress and announces each step;
you can cancel during the download (nothing is changed), but not during the
brief install step at the end.

Once installed, winget stays current on its own: on PCs with the Microsoft Store
it is updated by the Store like any other app, and anywhere else
`winget upgrade Microsoft.AppInstaller` updates it in place.

## The Settings page

The gear item at the bottom of the left navigation (labelled **Settings**)
opens GUARD's own settings. Changes here take effect and are saved the moment
you make them; there is no Save button. They live in `guard-prefs.ini` next to
`GUARD.exe`, so they travel with the folder like everything else.

### Updates

**Check for updates automatically** (Alt+U, on by default): once a day, when
GUARD starts, it quietly asks GitHub whether a newer release exists. If the
check cannot reach the internet it simply tries again on a later start.

When a new version is found, a notice appears just above the status bar (and is
announced to screen readers). Choose **View Update** on the notice to see what
is new in that release, then pick one of:

- **Install and Relaunch** (Alt+I): downloads the new version, verifies the
  download against the release's published checksum, closes GUARD, applies the
  update, and reopens it. Your settings, generated scripts, and logs are never
  part of the download, so they are untouched.
- **Remind Me Later** (Alt+R, or Esc): does nothing; the next daily check
  offers the version again.
- **Skip This Version** (Alt+S): that version is not offered again by the
  automatic check; the next release after it will be. A manual check always
  shows the newest version, skipped or not.

**Download updates automatically and install them when GUARD exits** (Alt+A) is
the hands-off mode: a found update downloads in the background and applies
itself after you close GUARD (it does not reopen GUARD afterwards).

**Check for Updates Now** (Alt+C) checks immediately and always reports a
result, up to date or otherwise. The About dialog carries the same button
(Alt+U there).

Self-updating needs the GUARD folder to be writable. If you have placed GUARD
somewhere it cannot write (for example under `C:\Program Files`), GUARD says so
and points you to the Releases page to update by hand. If you installed through
winget, `winget upgrade` also works, as before.

### Appearance

Choose **System setting** (the default) to follow Windows' light or dark mode,
or pin GUARD to **Light** or **Dark** regardless of the system.

### Startup

**Page shown when GUARD opens** (Alt+P) picks which page is selected at launch:
File Backup (the default), System Image, or App Management.

### Notifications

These control Windows notifications (toasts) about backups that run
*unattended* - the scheduled backup and the on-connect check. Backups you start
inside GUARD announce themselves in the app instead.

- **When it fails or cannot run** (Alt+F, on by default): a notification when
  an unattended backup finishes with errors, or a scheduled backup cannot
  reach its destination. This is the one worth keeping on: a silent nightly
  failure is exactly what a backup tool must not allow.
- **When it succeeds** (Alt+G, off by default): also confirm each clean
  unattended run. Useful for peace of mind on a new setup; most people turn it
  off once they trust the schedule.

The scheduled *system image* runs as the SYSTEM account, which Windows does not
deliver notifications from; its outcome shows in the System Image page's status
line the next time you open GUARD. The first notification GUARD shows registers
its name with Windows notifications - a one-time entry for your user account,
and the one thing GUARD writes outside its folder besides the scheduled tasks.

### Scheduled tasks

GUARD's schedules are registered with Windows Task Scheduler, not stored in
GUARD's folder. That means deleting GUARD - or uninstalling it with
`winget uninstall` - leaves them behind, waking up on their timer to run an app
that is no longer there.

**Remove GUARD's Scheduled Tasks** (Alt+T) unregisters all of them: the
scheduled backup, the on-connect check, and the scheduled system image. Do this
before you delete or uninstall GUARD.

Your generated scripts and your existing backups are untouched. The schedules
are switched off in your settings to match, so GUARD and Windows agree; turn
them back on any time with **Save Settings**. The system image task runs as the
SYSTEM account, so removing that one asks for Administrator approval - and only
when such a task actually exists. Choose **Leave it** and the image task stays
registered and still runs; GUARD says so rather than reporting a clean sweep, and
its schedule stays switched on so the two still agree. If Windows refuses to
remove a task, GUARD says which one and leaves your settings alone so you can
try again.

### App reinstalls

This card appears only when winget is missing from the PC. **Install winget**
(Alt+W) opens the same install dialog as the notice on the App Management tab;
see [Installing winget](#installing-winget).

## The status bar

A status bar runs along the bottom of the window. It shows the active tab's
status (the backup-health line on File Backup, the scan or import summary on App
Management) and, while a backup or reinstall is running, a compact progress bar
with the current action, so progress stays visible even when the in-tab progress
area is scrolled away or another tab is active. After a job ends, the bar keeps
that run's outcome until the next job starts.

Once your settings are saved, the File Backup and System Image status lines
answer the question that actually matters: **is this PC protected?** They
report how the last run ended and when ("Last backup succeeded yesterday at
02:00"), and the status dot turns amber when something needs attention: the
last run failed or did not complete, a scheduled run is overdue, or an
on-connect backup has not managed to run in over a week (time to plug the
drive in). Before the first run, and after each save, the line instead shows
the estimated backup size against the destination's free space.

The status bar is exposed as an actual status bar element, so a
screen reader's read-the-status-bar command reads it on demand, including a
running job's progress text.

## Where your files live (portability)

Everything GUARD knows lives in its own folder, next to `GUARD.exe`:

| File | What it is |
|---|---|
| `backup-settings.ini` | All your backup settings: destination, folder list, mode, exclusions, schedule. Plain text. |
| `guard-prefs.ini` | GUARD's own preferences from the Settings page: update options, theme, startup page. Plain text. |
| `guard-backup.cmd` | The generated backup script. Regenerated on every Save Settings. Theoretically you could run this script portably from anywhere to do an on-demand backup. |
| `guard-system-image.cmd` | The generated system-image script, if you use the System Image tab. |
| `onconnect-stamp.txt` | Records the last day an on-connect backup ran, so it runs at most once a day. Only appears if that option is on. |
| `Logs\backup_last.log` | The log of the most recent real backup run. |
| `Logs\backup_preview.log` | The log of the most recent Preview (dry run), kept separate so a preview never overwrites the real backup log. |
| `Logs\backup-running.lock` | Present while a backup runs (it stops two backups running at once); an empty leftover is harmless. |
| `Logs\system-image_last.log` | The log of the most recent system image. |
| `Logs\image-versions_last.log` | The output of the most recent List Images query. |
| `Logs\recovery-media_last.log` | The log of the most recent recovery-media (bootable USB) build. |
| `Logs\app-update_last.log` | The output of the most recent Update All Apps run. |
| `Logs\update_last.log` | The log of the most recent self-update. |
| `Logs\crash_last.log` | Written only if GUARD ever crashes; include it when reporting a problem. |
| `Logs\debug_last.log` | Optional diagnostics, written only when a `debug.flag` file sits next to GUARD.exe (or in GUARD's settings folder). |
| `USER_GUIDE.html` | This manual; the Help button (F1) opens it in your browser. |

**One exception: GUARD installed with winget.** winget owns the folder it
installs into and deletes it outright on `winget upgrade` and
`winget uninstall` - so anything GUARD wrote there would be destroyed by the
next upgrade. A winget install therefore keeps its settings, generated scripts
and logs in `%LOCALAPPDATA%\GUARD` instead, and updates through
`winget upgrade PlanetLinux98.GUARD` rather than updating itself. A GUARD folder
you extracted yourself is unaffected by all of this and stays fully portable.

Because everything is in one folder, **moving the folder moves the app and its
settings together**: copy the `GUARD` folder to another PC or a USB stick and
your configuration comes along. The scheduled tasks are the one thing tied to
the original location, and GUARD repairs them itself: the next time you open
GUARD from the folder's new home, the backup and on-connect tasks are
re-registered automatically (on a different PC, that first launch registers
them fresh). Only the scheduled system image needs you to click **Save
Settings** once, because repointing a SYSTEM task requires Administrator
approval - the System Image page tells you when that applies.

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
- The **Mode** radio options follow the same expected convention:
  arrow keys move and select at once.
- **Alt access keys** reach the important controls directly (the access key for
  each control is given in parentheses throughout this manual), and **F1** opens
  this manual from anywhere.
- **Ctrl+1 to Ctrl+4** switch pages from anywhere in the window: Ctrl+1 File
  Backup, Ctrl+2 System Image, Ctrl+3 App Management, Ctrl+4 Settings. Focus
  lands on the page in the navigation, so you can arrow to a neighbouring page
  from there.
- **At launch, focus starts on the navigation** (the current page's item), so
  the first thing you reach is the page list rather than an empty pane.
- **Dark and light Mica theming** follows your Windows setting automatically,
  or can be pinned to Light or Dark on the [Settings page](#the-settings-page).

If something reads or behaves badly with your screen reader, that is treated as a
serious bug; please report it on the
[issues page](https://github.com/PlanetLinux98/guard/issues).

## Frequently asked questions

**Is my data sent anywhere?**
No. GUARD has no telemetry, no account, and no cloud component. Backups and exports 
go only to the destination you choose. The only internet-reaching network (WAN) 
activity GUARD can cause is winget contacting its official package sources during 
app reinstalls and updates, the optional winget install downloading Microsoft's 
package from the official winget release, and the update check asking GitHub for 
the newest release number (which sends nothing about you or your PC, and can be 
turned off on the Settings page).

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
Turn off any scheduled backup and image (untick the schedule options and Save
Settings on each page, or delete the GUARD tasks in Task Scheduler), then delete
the `GUARD` folder. GUARD stores nothing else, apart from the one-time
notification registration (a single per-user registry entry, harmless to leave).
Your backups at the destination are untouched.

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
anyway**. See [Download manually](#download-manually).

**Save Settings asks me to pick a day.** The schedule is on but no weekday is
ticked. Tick at least one day, or turn the schedule off.

**The scheduled backup did not run.** Check that the PC was on at the scheduled
time, that **Next run** shows a time after saving, and look in Task Scheduler
for **GUARD Backup**. If you moved the GUARD folder, Save Settings again so the
task points at the new location. **Open Last Log** shows whether a run happened.

**Some folders report errors in the log.** Common causes are files locked by a
running program or paths needing permissions your account lacks; the log names
each failing file. Junction points such as the hidden "My Music" links inside
Documents are skipped automatically and are not errors.

**winget is reported as not installed.** Scanning and exporting still work. To
get automatic reinstalls, press **Ctrl+I** (or use the **Install winget** button
on the notice above the status bar, or on the Settings page); GUARD downloads
Microsoft's App Installer package and sets winget up for your user account. See
[Installing winget](#installing-winget).

**Reinstalls fail with an access error.** Run GUARD as Administrator and try
again; see the admin note in
[Importing on a new PC](#importing-on-a-new-pc-reinstall-and-restore).

**A settings restore did not change anything for an app.** The app may have been
running (running-app folders are skipped), or its settings live
somewhere GUARD does not copy (the registry, ProgramData, or a Store packaged-app
container). Close the app and restore again, or restore those by hand.

## For developers

GUARD is an SDK-style C# project targeting .NET 10 and Windows App SDK 2.2
(WinUI 3), shipped unpackaged and self-contained. Build, project layout, and
contributing notes are in the [README](README.md). The repo is trunk-based, with
short-lived branches off `main` and a PR per change; accessibility reports are
especially welcome.

GUARD is released under the MIT License; see [LICENSE](LICENSE).

---

*GUARD is developed by [PlanetLinux98](https://github.com/PlanetLinux98/guard).*
