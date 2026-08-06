# Changelog

All notable changes to GUARD are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While GUARD is in the `0.x` series, behaviour may change between minor versions.

## [Unreleased]

### Changed
- The README now lists the build prerequisites in one table, separating what a
  development build needs from what only the shipping NativeAOT build needs, and
  notes that the clone must carry tags for MinVer to version a build correctly.

### Fixed
- A folder pair whose destination subfolder was `.` (or started with `.\`) backed
  up to the destination root rather than to its own folder, and the Mirror-mode
  collision check did not recognize it as the root, so the save was allowed and
  the next run's `/MIR` deleted every other folder's backup. Destination
  subfolders now resolve the same way in the check and in the generated script.
- A run whose source folders were all missing (an external drive that did not
  mount, a share that was down) still finished OK, so the status line stayed
  green and an unattended run reported that the backup had succeeded. A source
  that is not there is now reported as an error rather than skipped silently.
- Self-update waited only for the GUARD window to close, not for a scheduled
  backup running in its own GUARD process, so an update applied while one was
  running could unpack over files still in use and leave the folder part old and
  part new. It now waits for every GUARD process, and abandons the update rather
  than unpacking if one will not exit.
- The scheduled system image reported success whether or not Windows accepted the
  change, so an image schedule could keep firing after it was switched off, or
  never be registered after it was switched on, while the page showed the
  schedule as saved and Save was disabled so it could not be retried. Switching
  it off is now verified, a failed apply says so and leaves Save available, and a
  schedule whose task has gone missing is spotted at launch.

## [0.5.3] - 2026-08-01

### Added
- Settings: "Remove GUARD's Scheduled Tasks", which unregisters GUARD's
  schedules from Windows. Run it before deleting or uninstalling GUARD, or the
  tasks stay behind firing at an app that is gone.
- GUARD warns when a folder has nothing left to back up while your backup still
  holds files from it - in the status line, on save, on each run, and in the
  notification for unattended runs. "Don't warn again" silences a folder you
  emptied on purpose; in Mirror mode the warning is sharper, because the next run
  deletes those copies. A folder GUARD cannot read is reported as unreadable
  rather than empty.
- GUARD warns when the backup destination is empty but its records show a backup
  has run, which means the backup was deleted or the drive reformatted.

### Changed
- Installed with winget, GUARD now keeps its settings, scripts and logs in
  `%LOCALAPPDATA%\GUARD` and updates through `winget upgrade` rather than
  updating itself: winget deletes its whole install folder on upgrade and
  uninstall, destroying anything kept there. That deletion happens before this
  version first runs, so set your settings up once more afterwards and they will
  survive from then on. A folder you extracted yourself stays fully portable.

### Fixed
- The default backup folders were fixed paths (`%USERPROFILE%\Documents` and so
  on), so once Windows moved one - OneDrive's "Back up your folders", or the
  folder's Location tab - GUARD backed up the empty folder left behind and still
  reported success. GUARD now tracks which Windows folder each default row
  follows and offers to follow a move.
- Every date GUARD showed or wrote followed the system locale's calendar, so on
  a Thai or Arabic Windows the "Next run" times, the status line, exported file
  names and the versioned backup folders all carried Buddhist or Hijri dates.
  The folder names were the dangerous one: pruning picks the oldest by name, so
  a later locale change could make it delete the newest backups first. If you
  are on such a Windows with existing versioned backups, delete the old
  wrongly-named folders once - they would otherwise count as your newest
  versions for ever.
- Online-only OneDrive files could be left out of backups: the generated script
  excluded file-level reparse points along with the directory junctions it meant
  to skip, and size estimates ignored them for the same reason. If this affected
  you, expect a long first backup while OneDrive downloads them.
- Folder paths in warnings and notifications were shown unexpanded, so a screen
  reader read out "percent USERPROFILE percent backslash".
- Two portable copies of GUARD installing winget at once shared one download
  folder, so each could wipe the other's half-finished download.
- Building recovery media, and "View Existing Images", failed with nothing to
  show on a copy where no backup had been saved yet: both write their progress
  straight into a `Logs` folder that only a saved backup had created.
- GUARD installed via `winget install` would not launch, and a scheduled backup
  task saved under that install would silently never run: winget's portable
  packages launch through a symlink Windows reports as the app's real location,
  so GUARD could not find its own files. GUARD now resolves its real install
  folder before anything else runs.

## [0.5.2] - 2026-07-22

### Changed
- Byte sizes in the end-of-run backup summary now use the same compact
  formatter as the settings-folder lists, and sizes above 1 TB display in TB.

### Fixed
- Nothing stopped a backup, system image, winget reinstall/update,
  app-settings export, or app scan from running at once across pages;
  robocopy, wbadmin and winget could race each other with stacked elevation
  prompts. Each page's Run/Start action now checks whether another is
  already running and names it.
- The backup destination could silently switch to an unrelated drive: a
  reachable but unrecognized volume at the last-used letter (e.g. the real
  drive was unplugged and something else took its letter) was adopted
  without asking, risking the new drive's files being deleted as "extras" in
  Mirror mode. Save now warns and needs a second Save to confirm.
- Restoring app settings could lose the original folder: if putting the
  saved copy back failed right after the existing folder was renamed aside
  to make room, GUARD reported it as merely "skipped" even though the
  original sat renamed under `<name>.guard-old-<timestamp>` beside it. GUARD
  now tries to move the original back first, and only reports it separately
  (with its location) if that also fails.
- A backup run with a robocopy "Mismatch" (e.g. the destination has a file
  where the source now has a same-named folder) was reported as a clean
  "Backup complete" even though robocopy couldn't reconcile it. Mismatches
  are now counted and called out in the run summary alongside failures.
- An imported app-settings manifest could target any environment-variable
  folder on the machine (such as `%USERPROFILE%\Desktop`) for
  rename-aside-and-replace during a restore, since only the folder name
  inside the anchor was checked, not the anchor itself. Restoring now only
  accepts the three anchors GUARD's own export writes (`%APPDATA%`,
  `%LOCALAPPDATA%`, `%USERPROFILE%\.config`).
- A locked or momentarily unreadable settings or preferences file (an
  antivirus scan, the file open elsewhere, a laggy portable or network copy)
  could crash GUARD on launch, or crash the headless scheduled backup task
  silently. Both now fall back to defaults, as if the file didn't exist.
- The recovery-media wizard is now more robust against interruption: closing
  GUARD while it's writing a USB is blocked (the elevated build can't be
  stopped by closing, which used to abandon diskpart/DISM mid-write with the
  progress and "do not remove the drive" warning gone); and a preparation
  error before the elevated step starts now lands on the wizard's own "Could
  not finish" result instead of crashing GUARD outright.
- The Recovery Media wizard's USB list could show a user's own external hard
  drive (the same kind GUARD's File Backup and System Image features back up
  to) with no visible difference from a blank flash stick, since both report
  the same USB bus type. A drive much larger than a typical recovery stick
  now shows a warning and needs an extra tick before it can be erased.
- Closing GUARD while "Update All Apps" was still running with Administrator
  rights gave no warning, even though the close prompt already had wording
  for exactly this case; it never actually triggered.
- A backup log's outcome could be misreported as "did not complete" after a
  large version prune: BackupScript logs one line per deleted folder after
  the FINISHED marker, and enough pruning could push that marker past the
  fixed 16 KB window the status check read. The check now searches
  progressively larger windows until it finds the marker.
- The scheduled and on-connect backup process had no timeout: a stuck run
  (e.g. robocopy hung on a flaky network share) could block every later fire
  indefinitely with no toast telling you backups had stalled. It's now
  killed and reported as a failed run after 12 hours.
- The installed-app scan could report an app as already up to date when
  winget actually had no idea what version was installed: a row with a
  blank Version cell but a populated Available (upgrade) cell parsed the
  same as a normal row, mistakenly storing the available version as the
  installed one.
- A fast double-click on Reinstall Selected or Update All Apps could start
  two overlapping runs: the busy flag was only set after the
  winget-availability check and confirmation dialog had been awaited,
  leaving a window for a second click to slip past the guard; the same race
  Run Now already closed.
- App settings export and restore had two accuracy gaps: a symlinked or
  hard-linked file inside a copied folder was followed instead of skipped
  like the folders around it (matching robocopy's own /XJ), and a subfolder
  that failed to enumerate (a permissions error) was counted as one skipped
  file instead of an abandoned subtree. Both are now handled and counted
  correctly.
- A scheduled-task registration error with a multi-line message had all but
  its first line silently dropped from what GUARD showed; the rest is now
  kept.
- Non-ASCII characters (accented paths, drive names, PowerShell error text)
  could get mangled throughout GUARD's shell-based tooling: cmd reads
  generated scripts in the legacy OEM codepage, and PowerShell writes the
  same codepage to a redirected pipe instead of UTF-8, which could fail
  backups ("destination not reachable"), skip folders, break self-update for
  non-ASCII usernames, and garble captured errors. Both the generated
  scripts and PowerShell calls now switch to UTF-8; existing scripts pick up
  the fix on the next save.
- Self-update also failed when GUARD was installed at a drive's root (such
  as the top of a USB stick) or under a path containing a literal %, since a
  bare drive letter reads as relative once quoted and cmd treats an
  unescaped % as a variable. The apply script now normalizes the root and
  escapes the %.
- A GitHub release response with no assets list at all (unlikely, but not
  ruled out by the API) would have crashed the update download with a
  null-reference error, instead of falling through to the normal "no
  compatible download" message.
- With a pinned Light or Dark theme, the small follow-up dialogs (validation
  messages, file-picker and browser-launch errors) now follow it instead of
  the OS theme: a dialog renders in its own popup layer that doesn't inherit
  the window's override, so each one now has it mirrored on explicitly.
- Opening the System Image page on a Windows edition without wbadmin (e.g.
  Home) no longer invents unsaved changes (and a save prompt on close) when
  it disables a saved image schedule: the automatic untick was wrongly
  firing the same dirty flag a real edit would.
- The status bar could lose a fresh status to a stale one: a size and
  free-space check that finished after the line had been rewritten (e.g. a
  quick backup ran while it measured) put the old text back regardless of
  which was newer. A superseded check's result is now discarded.
- The System Image page could show "Next run: (unknown)" for a healthy
  scheduled image, since the label only refreshed when a save changed the
  schedule; the next run time now also loads when GUARD opens.
- The scheduled system image failed to register on Windows locales whose
  default calendar isn't Gregorian (such as Thai or Umm al-Qura), since a
  plain date format follows the OS calendar and Task Scheduler rejects
  anything else. The task's start date is now always written as an
  invariant Gregorian date.
- Saving or creating a system image now warns when the destination contains
  a % that is not an environment variable, as File Backup already did; cmd
  treats % specially in the generated script, so the image could be written
  to the wrong location.

## [0.5.1] - 2026-07-09

### Added
- GUARD can now install winget itself on PCs that lack it (LTSC, Server
  editions, or no Microsoft Store): an "Install winget" notice appears above
  the status bar (Ctrl+I) when the app scan finds it missing, the Settings
  page has the same button, and reinstalling from a saved list offers it
  first. GUARD sideloads Microsoft's signed App Installer package from the
  official winget release for the current user; no Administrator approval
  needed, and Windows verifies the signature.
- Backup health in the status bar: the File Backup and System Image pages now
  report how and when the last run ended (e.g. "Last backup succeeded
  yesterday at 02:00") instead of just "settings saved". The status dot turns
  amber if the last run failed or didn't complete, a scheduled run is
  overdue, or an on-connect backup hasn't run in over a week.
- Windows toast notifications for unattended backups: failures notify by
  default, successes are opt-in (Settings > Notifications). Runs started from
  inside GUARD stay silent, and the scheduled system image reports through
  the status line instead (Windows doesn't deliver notifications from SYSTEM
  tasks). The first notification registers GUARD's name with Windows (one
  per-user registry entry).
- The backup destination now follows its drive if the letter changes: saving
  records the volume's serial number, the generated script re-finds it by
  serial at run time if the saved letter is gone (logging the change), and
  the next save re-anchors the destination to the new letter.
- Update All Apps (App Management page) runs `winget upgrade --all` with one
  upfront Administrator approval, so all-user and Store-delivered packages
  (like WSL) can update too. Output streams to the Output details area,
  followed by a quiet rescan.
- Ctrl+1 to Ctrl+4 switch pages from anywhere in the window (File Backup,
  System Image, App Management, Settings).
- List Images (beside the System Image destination field) lists existing
  images on that destination via `wbadmin get versions` (one Administrator
  approval per use).
- Save Settings and Run Now warn when the destination or a source path
  contains a % that is not an environment variable: Windows command scripts
  treat % specially, so the generated script would silently misread the path.
- Optional diagnostics: a `debug.flag` file next to GUARD.exe (or
  GUARD_DEBUG=1) logs background failures GUARD normally swallows to
  `Logs\debug_last.log`; a crash always writes `Logs\crash_last.log`.
- A unit-test project (`Tests\GUARD.Tests.csproj`, not shipped) covering the
  backup-script generator, the winget list parser, the robocopy summary
  parser, update version comparison, schedule arithmetic, and save validation.

### Changed
- Fresh installs no longer pre-tick the "Developer folders" exclusion preset:
  it skips every folder named bin, obj, .git or node_modules anywhere in the
  backup, and those names ("bin" especially) can carry real user data.
  Existing saved settings keep whatever presets they have.
- The offline manual now ships as `USER_GUIDE.html` (styled, self-contained,
  follows system light/dark mode) instead of `USER_GUIDE.md`, so Help (F1)
  opens it in the default browser rather than a "how do you want to open this
  file?" prompt on PCs with nothing associated with .md. Updating cleans up
  the old `USER_GUIDE.md` automatically.
- The backup size estimate and in-run byte progress now honour exclusions, so
  the figure reflects what a run actually copies; previously large excluded
  trees (like `node_modules`) could trigger false "space may be too low"
  warnings.
- The Test buttons now ask before creating a missing destination folder
  instead of creating it silently.
- Settings files and the generated scripts are now written crash-safe (to a
  temp file swapped into place), so a crash or power cut mid-save can no
  longer leave a truncated ini or script behind.
- The installed-app scan's winget enrichment now recognizes the list header
  on localized (non-English) Windows, where every app was previously reported
  as "Manual".
- Scheduled and on-connect backups now run with no visible window: the tasks
  launch GUARD.exe in a hidden helper mode instead of cmd.exe, so the
  on-connect check (every 15 minutes) no longer flashes a console while
  signed in. Existing tasks migrate automatically at the next launch or save.
- The app scan now reads winget's Source column, labelling Microsoft Store
  apps as "Store" instead of "Winget", so an exported list is honest about
  which apps need the Store. When both winget and the Store carry an app, the
  winget id is preferred, since it also works on editions without the Store.
- Only one backup can run at a time: the generated script takes a run lock,
  so a scheduled run firing during an in-app run (or vice versa) skips
  cleanly instead of interleaving two robocopy passes over the same log. The
  script also reports distinct exit codes (0 ok, 1 errors, 2 could not run,
  3 nothing to do) for Task Scheduler history and notifications.
- A second GUARD.exe launched from the same folder now brings the already-open
  window to the front instead of running twice against the same settings
  files.
- The System Image destination caption now says that a folder typed after a
  local drive letter is ignored (wbadmin always writes to the drive's root,
  into WindowsImageBackup).
- An update now refuses to install when the release has no SHA256SUMS
  checksum file, instead of quietly installing unverified; a missing manifest
  means a mis-published release.

### Fixed
- Several ways GUARD could crash or lock up: a failed settings save (e.g.
  GUARD run from a read-only folder) crashed instead of showing an error;
  clicking Run Now or Preview again before the first click's setup finished
  could start two backups at once; a failed folder or file picker crashed
  instead of failing the browse; and an unexpected error during a reinstall
  or settings restore left the App Management buttons disabled for the rest
  of the session.
- Save Settings now blocks two setups that could never produce a good backup:
  a Mirror-mode configuration whose folders share, nest, or root their
  destination subfolders (each run purged the other's files as "extras"), and
  a fully-unticked folder list, which saved and scheduled a backup that
  copied nothing while reporting success.
- Custom exclusions can no longer contain characters cmd treats as operators
  (& ^ < > %), which corrupted the generated script's robocopy options, and
  destination subfolder names likewise reject %.
- Values loaded from hand-editable files are now re-validated: folder and
  exclusion entries in backup-settings.ini get the same checks as the add
  dialogs, and an imported app-settings bundle's manifest can no longer name
  folders outside the bundle or outside the settings roots it restores to.
- Elevated actions (System Image, Recovery Media, scheduled-task
  registration) are more reliable and safer: the script now travels inside
  the PowerShell command itself rather than through a temp file, which
  removes both the encoding trap that corrupted non-ASCII account and
  install paths and the brief window in which another program could have
  tampered with the file before Administrator approval (the elevated
  scripts' own diskpart and schtasks work files moved out of user-writable
  temp for the same reason). An apostrophe in GUARD's folder path no longer
  breaks Create Image Now, and a launch failure is only reported as
  "Administrator approval was declined" when that is what happened.
- A hung winget or PowerShell no longer pins its page's job for the rest of
  the session (a scan stuck on "Scanning...", a save that never finished):
  captured helper processes now have a hard deadline and are stopped and
  reported when they exceed it.
- The close-time "a job is still running" warning no longer appears for a
  job that finished while the unsaved-changes prompt was open.
- Skipping an offered update now also cancels a copy of that version that
  auto-install had already staged (it previously installed on exit despite
  the skip); a failed or cancelled re-download clears the stale staged
  install; and each portable copy of GUARD stages updates in its own temp
  folder, so two copies can no longer apply each other's update.
- A moved or renamed GUARD folder no longer silently breaks scheduled
  backups: the backup and on-connect tasks re-register against the new
  location at launch, and the System Image page flags its SYSTEM task for
  repointing at the next save (with the usual Administrator approval).
- Preview no longer overwrites the real backup log: it writes to its own log
  file, so a preview can no longer be mistaken for the last real backup in
  the status bar or Open Last Log.
- Run Backup Now now reports a scheduled-task registration problem from the
  save it performs, as Save Settings already did; the on-connect task now
  notifies when the backup script itself is missing instead of failing
  silently every check.
- Progress and error lines tailed from the elevated system-image and
  recovery-media logs are no longer lost when a line arrives split across two
  reads, which could drop the ERROR line explaining a failed USB build.
- The app scan no longer shows an app twice when winget truncated its name
  (the truncated row now merges into the matching installed app), and an app
  with an empty winget Version cell no longer picks up the next column's
  value as its version.
- A winget id containing quotes in an imported `app-list.json` can no longer
  splice extra arguments into the reinstall command; ids are passed as data.
- Exporting the app list now writes it the same crash-safe way as every other
  GUARD-generated file, so an interrupted export can no longer leave a
  corrupt app-list.json.
- Closing GUARD while exporting the app list, scanning installed apps, or
  listing existing system images now prompts like a running backup does,
  instead of closing silently mid-job.
- A USB drive was silently left out of the Recovery Media wizard's drive list
  if its manufacturer-supplied name happened to contain a "|" character.
- Toggling a checkbox or radio button with its Alt access key went
  unannounced by NVDA, unlike Tab+Space: the access key toggled the control
  without moving keyboard focus to it, so every access-keyed toggle now moves
  focus first. The Recovery Media wizard's Back button also collided with
  Browse on Alt+B and is now Alt+P.
- Silent status repaints from a background page no longer disturb the active
  page's screen-reader announcement tracking, which could re-announce an
  unchanged status or drop a changed one.
- Keyboard focus now starts on the navigation at launch instead of the empty
  pane, so a screen-reader user isn't left on "GUARD, pane" needing an extra
  Tab.
- The Open Manual button in the System Image restore-help dialog, and the
  Project Page link in the About dialog, now show an error message instead of
  doing nothing when there is no program available to open them.
- The default window is now 840 DIP tall instead of 900, so it's less likely
  to run under the taskbar on smaller or scaled displays.
- Corrected out-of-date details in the manual: the Windows App SDK version,
  the Help item's location, a broken Troubleshooting link, and the
  portability table now also lists the system-image, recovery-media, and
  on-connect files. The README's outgrown Planned Features section is
  removed; its top now leads with a links line (User Manual, Releases,
  Changelog).

## [0.5.0] - 2026-07-02

### Added
- An original app icon: a glossy blue glass shield carrying a bold white G with
  an amber drive-activity light, shown in Explorer, on the taskbar, in the title
  bar, in the Alt+Tab switcher, and at the top of the About dialog.
- Built-in updater. Once a day when GUARD starts it quietly checks the project's
  GitHub Releases for a newer version (on by default; configurable on the new
  Settings page). When one exists, a notice appears above the status bar (and is
  announced to screen readers) with a View Update button (Ctrl+U jumps straight
  to it from anywhere in the window); the update dialog shows that release's
  notes, converted to plain readable text so screen readers are not fed raw
  markdown, and offers Install and Relaunch, Remind Me Later, or Skip
  This Version (a skipped version is not offered again automatically, but a manual
  check always shows the newest release). Installing downloads the release zip,
  verifies it against the release's published SHA-256 checksum, applies it after
  GUARD closes, and reopens GUARD; settings, generated scripts, and logs are
  untouched. An optional hands-off mode downloads updates in the background and
  installs them when GUARD exits. Check for Updates Now is available on the
  Settings page and in the About dialog. Release builds now also produce a
  SHA256SUMS manifest, attached to each GitHub Release for the verification step.
- Settings page, reached from the standard gear item at the bottom of the left
  navigation. Changes save the moment they are made (no Save button). It holds
  the update options, an Appearance choice (follow Windows' light or dark mode,
  or pin GUARD to one), and the page GUARD opens on at startup. Preferences are
  stored in a new `guard-prefs.ini` next to `GUARD.exe`, so they travel with the
  folder like everything else.
- A small progress ring now appears on a page's entry in the left navigation while
  that page has a job running (a backup, a system image, or an app export/reinstall),
  so you can tell at a glance which page is busy even while viewing another page. The
  ring fills to show how far along the job is once a percentage is known, and spins
  while a step has no measurable progress. It clears when the job finishes. Screen
  readers announce the page as "running" when focused, and the existing completion
  announcement is unchanged.
- New System Image page (second in the navigation) creates full bare-metal images
  of the whole PC using the built-in Windows `wbadmin` tool, so a machine can be
  recovered after a disk failure. Images go to a local or external disk (which
  keeps several past images automatically) or a network share (which keeps only
  the latest). Create one on demand (one Administrator prompt) or on a Weekly,
  Monthly or Daily schedule that runs as SYSTEM with the highest privileges, so it
  needs no prompt at run time. Imaging self-disables on Windows editions without
  `wbadmin` (such as Home), and refuses a destination on the Windows drive itself.
- Create Recovery Media wizard on the System Image page builds a bootable Windows
  installation USB used to restore an image, with built-in tools only (it detects
  this PC's architecture and Windows version, formats the chosen removable drive as
  FAT32, copies the installer, and splits a large `install.wim` so it fits). Only
  removable USB drives are offered and the drive is confirmed before it is erased.
  Restore Instructions explains the offline restore through Windows' own System
  Image Recovery, including how to reach a network share from the recovery
  environment (which addresses a server by its IP address, not its name, and asks
  for the share's username and password). When the destination is a network share,
  GUARD shows the exact IP path to enter, both in Restore Instructions and after an
  image is created.
- Closing GUARD with unsaved backup settings now prompts before exiting, with
  Save (Alt+S), Don't Save (Alt+N), and Cancel (Alt+C) - the familiar
  Notepad-style choice. Save writes the settings and then closes (staying open if
  a required value is missing so it can be fixed), Don't Save discards the edits
  and closes, and Cancel returns to the window. The existing warning about closing
  while a backup or reinstall is still running follows after, so both can apply to
  one close.
- Save now refuses a backup whose source folder contains the destination, or
  sits inside it, and explains which folder overlaps. Without the guard, such a
  setup could make Robocopy copy the backup into itself on every run, nesting
  `DEST\Sub\DEST\Sub\...` until the paths could pass Windows' 260-character limit
  and the folder could no longer be opened or deleted. Destinations that merely
  share a drive root with a source (e.g. `C:\Users\you\Documents` to `C:\Backup`)
  are unaffected.
- The backup progress bar now advances within each folder as bytes are copied,
  rather than only stepping once per folder. GUARD measures the included folders
  up front and a Run or Preview reads per-file byte counts from Robocopy to move
  the bar smoothly, falling back to per-folder steps if a very large tree cannot
  be measured in time. The extra per-file detail is added to interactive runs
  only; scheduled and unattended runs keep their compact log.

### Changed
- The status bar's progress area (its right-hand side) now follows the page you are
  viewing: it shows the current or most recent job of the focused page and repaints
  when you switch pages, instead of always showing the most recent job from any page.
  A job running on another page stays visible through that page's navigation ring.
- The Stop button on every page now uses the same Alt+T access key (Stop Backup and
  Stop Reinstall were Alt+C), so stopping a running job is the same keystroke
  whichever page you are on.
- Reworked the main window from a two-tab Pivot to a left navigation pane
  (NavigationView) with File Backup and App Management pages and Help and About
  as footer items (Help keeps its F1 shortcut). When the window is made narrow
  the pane collapses to a compact icon rail so the page keeps its width, and a
  toggle expands it again.
- Grouped each page's settings into titled cards with a pinned action bar along
  the bottom, so the primary actions (Save Settings, Run Now, Export, and so on)
  stay in view without scrolling. The advanced sections - Exclusions, Schedule
  and Output details - are collapsible expanders.
- Page content now fills the window width, left-anchored beside the navigation,
  and the folder and app lists' first column grows (up to a sensible cap) as the
  window widens so longer paths and names show. The window also has a minimum
  size, so the action bars and the App Management toolbar never clip.
- The Add Folder and Add Exclusion dialogs attach their helper text to the input
  field as screen-reader help text (spoken when focus lands on the field), and
  the visible caption is no longer read a second time as an out-of-context
  paragraph when the dialog opens.
- Status-bar messages too long to fit now show in full on hover.
- Slimmed the shipped folder so the portable app is no longer buried among dozens
  of unrelated files. The publish step now also strips the rest of the Windows AI
  / Copilot Runtime stack that Windows App SDK bundles but GUARD never calls (the
  on-device imaging, vision, semantic-search, Windows ML and content-safety
  projections and their workload manifests, on top of the ONNX/DirectML binaries
  already removed), and drops the WinUI framework's ~80 non-English localized
  resource folders (GUARD is English-only; the English ones are kept). The folder
  next to `GUARD.exe` goes from ~89 directories to 3, and the release zip is ~24
  MB (was ~28). No change for users beyond a tidier folder; reversible from the
  build script if a future feature or translation needs those components.
- The Save Settings button (on both the File Backup and System Image pages) is now
  disabled whenever the saved settings are already up to date, and re-enables the
  moment you change something (or when nothing has been saved yet). The greyed-out
  button is a clear at-a-glance cue that there is nothing to save and prevents a
  no-op save. Saving while the button has focus moves focus to Run Now / Create
  Image Now, so keyboard and screen-reader users are not stranded on a control that
  just became unavailable.
- When GUARD opens with backup settings already saved, the File Backup status line
  now fills in the backup size and destination free space on its own, instead of
  showing only "Settings saved" until the next manual Save. The System Image page
  does the same the first time it is opened. The check runs in the background so it
  never delays the window, and updates the status quietly without speaking over the
  opening window.
- Opening the last log (File Backup or System Image) when none exists yet now says
  "No log found yet. Run a backup first." instead of a bare "Not found:" followed by
  an internal file path.
- The File Backup status line now reads "File backup settings saved" rather than
  the bare "Settings saved", and "No file backup settings saved yet" rather than
  "No settings saved yet", matching the System Image page's wording so each page
  names which settings it means. Run Now with no saved script likewise says
  "Backup script not found. Click Save Settings first." instead of showing an
  internal file path.
- Run Now and Preview no longer re-save the settings when nothing has changed, so
  a run starts several seconds sooner (re-saving re-registered the scheduled tasks
  through PowerShell every time, even when they were already correct). The
  unreachable-sources note is still refreshed before each run.
- Closing GUARD while a job is running now says what closing means for that job:
  a backup or app reinstall is stopped, while a system image (which runs with
  Administrator rights) keeps running in the background.
- Internal code reorganization, no behaviour change: the main window's code-behind
  is split into per-page files (File Backup, System Image, App Management),
  superseded scheduled-task helpers that duplicated the batched save path were
  removed, and small helpers previously duplicated across files (byte-size labels,
  dialog access-key styles, visual-tree search) were consolidated.

### Fixed
- The generated guard-backup.cmd keeps its window open again after a double-clicked
  run, as its own usage notes promise. The "press any key" pause ran after the
  script's endlocal, which had already discarded the flag that requests it, so the
  window closed the instant the backup finished. Scheduled, on-connect and in-app
  runs are unaffected (they never pause by design).
- Exporting an app list, or saving System Image settings, no longer silently
  commits the other pages' unsaved edits to backup-settings.ini. Each save now
  writes only its own page's settings, so an edited-but-unsaved backup destination
  can no longer show up as "saved" on the next launch while the generated script
  still targets the old one.
- A source folder ending in a backslash (like D:\Data\ or a whole drive D:\) no
  longer breaks the generated robocopy command. Inside a quoted argument a trailing
  backslash reads as an escaped quote and mangles everything after it; sources are
  now normalized when the script is written (a drive root becomes D:\.), and the
  backup destination is normalized the same way.
- The Add/Edit Folder dialog now rejects quote and pipe characters (a quote breaks
  the generated script's quoting, a pipe silently truncates the entry in the
  settings file), and rejects subfolder names with invalid filename characters or
  ".." segments that would escape the destination root.
- A destination or source path containing & or other cmd operator characters no
  longer produces garbled "not recognized" lines in the backup output: the
  generated script now quotes paths wherever echo re-expands them.
- A hand-edited schedule time in backup-settings.ini is now normalized to HH:mm
  when loaded (falling back to the default when unparseable), instead of being
  passed verbatim into the PowerShell command that registers the scheduled task.
- GUARD run from the root folder of a drive (such as a USB stick's root) now finds
  its settings, script and logs correctly; the exe-folder path lost its trailing
  separator there and became a drive-relative path.
- Screen readers reading the progress line under "Output details" (on File Backup,
  System Image, and App Management) with the review cursor now read the current
  step, e.g. "Reinstalling 3 of 10", instead of a fixed label such as "Reinstall
  progress". A static accessible name had been overriding the live text.

## [0.4.1] - 2026-06-16

### Added
- First-letter navigation in the folder and app lists (both the main window and
  the Import, Export-settings and Restore dialogs): with focus in a list, type a
  letter to jump to the next row whose text starts with it, and press it again to
  cycle through the matches. Typing several letters quickly matches a longer
  prefix.

### Changed
- Tidied the Alt access keys. The Test buttons and the "Versions to keep" field
  no longer claim a mnemonic (both are a Tab away from an adjacent control), and
  on the App Management tab "Select None" is now Alt+N (was Alt+O) so "Open
  Folder" can take its natural Alt+O. "Select None" in the Import, Export and
  Restore dialogs moved to Alt+N to match.
- The folder list now shows each source as its resolved path (e.g.
  `C:\Users\you\Documents`) instead of the raw `%USERPROFILE%\Documents`, so the
  rows read clearly and first-letter navigation matches the real folder name.
  The saved path stays variable-based, so the generated backup script remains
  portable.
- The shipping build is now **NativeAOT** instead of ReadyToRun: faster startup
  (no JIT) and a much smaller download - the release zip is ~28 MB, down from
  ~81 MB. Built with `publish-release.cmd` (replaces `publish-singlefile.cmd`).
  NativeAOT cannot be a single `.exe`, so the release ships as the whole publish
  folder zipped - which GUARD already shipped as, so nothing changes for users
  (extract `GUARD.zip`, run `GUARD.exe`). Development builds stay JIT.
- Updated Windows App SDK from 1.8.260508005 to 2.2.0 (the newer, longer-supported
  2.x line).

### Fixed
- Enabling "Automatically run when the backup destination becomes available"
  (on-connect) and saving no longer fails with "On-connect task: Access is
  denied". The task's logon trigger was registered as an "any user" trigger,
  which Task Scheduler only permits an administrator to create; it is now scoped
  to the current user, so saving works without elevation.
- NativeAOT no longer crashes at startup (0xc000027b in Microsoft.UI.Xaml.dll the
  moment a data-templated list bound its `ItemsSource`) - which is what unblocked
  shipping an AOT build. The cause was CsWinRT silently omitting the `unsafe`
  marshalling stubs for the generic WinRT collection interfaces an
  `ObservableCollection<T>` of an app type implements when crossing the ABI; the
  fix adds `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` and marks the bound
  `INotifyPropertyChanged` models `partial`.

## [0.4.0] - 2026-06-15

### Added
- GUARD is now available through **winget**: `winget install --id
  PlanetLinux98.GUARD` (or just `winget install GUARD`). New releases usually
  appear in winget within a couple of days of the GitHub Release.
- A full **User Manual** (`USER_GUIDE.md`) covering setup and every feature and
  workflow, written for keyboard and mouse users alike. It ships inside the
  release zip, and the in-app Help button (F1) now opens it.
- App settings restore on the App Management tab. Import List now opens the
  saved list in its own dialog (instead of replacing the installed-apps list
  behind the tab) with the apps as tickable rows and the list's source machine
  and date. From there you can "Reinstall Selected" (apps only) or, when an
  `AppSettings` bundle was saved beside the list, "Reinstall & Restore Settings"
  - which reinstalls the ticked Winget/Store apps and then puts their settings
  folders back. The restore step shows a second confirmation of tickable rows
  with each folder's target location (re-anchored to your current user profile
  via the manifest, so it works even if the Windows username changed), whether a
  folder already exists, and its size. An existing target is renamed aside to
  `<name>.guard-old-<timestamp>` before being replaced, never deleted, so a
  restore is reversible. Settings are restored after the installs finish (before
  you launch anything), folders whose app is running are skipped and counted,
  and Stop Reinstall halts the whole operation. If none of the ticked apps
  reinstall automatically, the settings are restored on their own.
- App settings export on the App Management tab: tick "Also export app
  settings" and the Export action copies the ticked apps' settings folders
  alongside the app list, as one operation. GUARD matches the apps' names and
  publishers against folder names under `%APPDATA%`, `%LOCALAPPDATA%` and
  `%USERPROFILE%\.config`, shows the matched folders in a confirmation dialog
  of tickable rows (with Select All / Select None buttons, and sizes that
  calculate in the background while you review), and copies the confirmed ones
  into an `AppSettings` folder inside the export's dated folder, sorted by which
  root they came from, with a progress bar that advances by size as it copies. A
  JSON manifest and a plain-text README with restore instructions are written
  alongside. Cancelling the confirmation cancels the whole export, so a cancel
  never leaves a partial result. Cache subfolders, junctions, registry-stored
  settings, ProgramData and Store packaged app state are not copied; locked
  files are skipped and counted instead of failing the export.
- Optional "Keep dated backup versions" mode: each run copies into a dated
  folder (YYYY-MM-DD) inside the destination, and after a clean run the oldest
  dated folders beyond a configurable keep count (default 5) are pruned. Off by
  default; the generated script is unchanged unless you opt in. Pruning only
  ever touches folders directly under the destination whose names exactly match
  the date pattern.
- New "Automatically run when the backup destination becomes available" option:
  a second scheduled task ("GUARD On-Connect Backup") quietly checks for the
  destination every 15 minutes and at sign-in, and runs the backup at most once
  per day when it is reachable - so plugging in the external drive (or the
  network share coming back) is enough to get that day's backup. Works with or
  without the day/time schedule, and like all GUARD backups it does not need the
  app open.
- Save Settings now reports the destination's free space, a rough size for a
  first full backup, and any included source folders that are not currently
  reachable (these are skipped at run time, so an offline network source is
  fine). The figures appear in the status line and are announced to screen
  readers, calculated in the background so they never hold up the save, with a
  warning (and an amber status indicator) when space looks tight. Run Now and
  Preview note any unreachable sources in the output box rather than
  interrupting the run with a dialog.
- Stop buttons for both long-running jobs: "Stop Backup" on the File Backup tab
  cancels a running backup (the whole cmd/robocopy process tree is stopped), and
  "Stop Reinstall" on the App Management tab stops the winget reinstall loop after
  the current app (apps already installed stay installed). Each button sits with
  its tab's other actions and is enabled only while its job is running, and the
  output box and progress line report the cancellation instead of going silent.
  Keyboard focus follows the job: starting one moves focus to its Stop button,
  and when it ends focus returns to the button that started it. Screen readers
  hear the job begin (the first progress update) and end (the summary or
  cancellation message).
- A persistent status bar at the bottom of the window. It shows the active
  tab's status (the settings saved/unsaved line on File Backup, the scan or
  import summary on App Management) and, while a backup or reinstall is running,
  a compact progress bar with the current action, so progress stays visible
  even when the in-tab progress area is scrolled away or the other tab is
  active. The status bar is the screen-reader live region for status changes,
  and both the File Backup mid-page status line and the App Management scan
  summary moved into it. It is exposed to assistive tech as a real status bar
  (control type StatusBar), so screen reader users can invoke the read-status-bar 
  command (e.g. NVDA+End) to read it at any time, including the running job's 
  progress text. After a job ends, the bar keeps the outcome (the run summary, 
  "Backup cancelled.", or the reinstall result) in its progress slot until the next
  job starts, so the hotkey can always answer how the last run went.
- A plain-language summary at the end of every backup and preview run, built
  from Robocopy's own totals: files copied (with size), files skipped because
  they were already up to date, failures (called out first, with a pointer to
  the log), and extra destination files (noting whether Mirror mode removed
  them). The summary appears in the output box and on the progress line, so
  you no longer need to read the raw log to know how a run went.
- "Edit Folder..." button on the File Backup tab: change the source path or
  destination subfolder of an existing folder pair in place, instead of removing
  and re-adding it. The edit applies to the folder row that last held focus,
  matching how Remove Folder picks its target.
- A results count next to the App Management filter box (e.g. "42 of 187 apps"),
  so you can tell how many apps match as you type. It is announced to screen
  readers only while typing in the filter box, and only when the count actually
  changes.

### Changed
- The README is now development-focused (build, layout, contributing) and links
  to the new User Manual; end-user usage documentation moved into `USER_GUIDE.md`.
  The release zip and the in-app Help button now ship and open the User Manual
  instead of the README.
- Renamed the "App Inventory" tab to "App Management": with reinstalling apps
  and exporting their settings sitting alongside the inventory scan, the old
  name undersold what the tab does.
- The App Management tab's "Export List" button is now just "Export", since
  the one action covers the list and (optionally) the app settings.
- Reworked the exclude UI so no wildcard typing is needed for the common cases:
  four one-tick preset checkboxes (temporary files, system clutter, developer
  folders, caches and disc images) replace the free-text boxes, and anything
  else is added through a new "Add Exclusion" dialog that asks what to exclude
  (a folder name, a file extension, or a name/pattern) and builds the pattern
  for you. Custom exclusions appear in a list with Add/Remove buttons. Existing
  saved excludes migrate automatically: lines a preset covers tick that preset
  (which can also enable the preset's sibling patterns - review the checkboxes
  after upgrading), and the rest become custom entries.
- Tidied the File Backup tab layout: the exclude controls now sit directly
  below the Add/Remove Folder buttons (above the Mode choice), and the
  "Versions to keep" count sits on the same line as the "Keep dated backup
  versions" checkbox.
- Save Settings is now fast and no longer freezes the app: the scheduled tasks
  are registered in a single background step instead of several foreground ones,
  and a successful save is confirmed inline rather than with a pop-up dialog
  (dialogs now appear only for problems, such as a failed task registration).
  The "Next run" label also loads in the background instead of delaying the
  window at startup.
- The output consoles on both tabs now scroll automatically to the newest line
  while a backup or reinstall is running, without moving keyboard focus.
- Each export now goes into its own dated folder under the destination
  (`app-export-YYYY-MM-DD_HHMM`) holding the `app-list.json` and, when included,
  the `AppSettings` folder. Repeated exports never overwrite each other, and a
  list is always kept together with the matching settings (replacing the earlier
  numbered-filename scheme, which shared one `AppSettings` folder across exports).

### Fixed
- Screen readers announced the static label ("Inventory status" / "Settings
  status") instead of the actual message whenever a status line updated, e.g.
  after the app scan finished. The status text itself is now announced.
- Exclude names containing spaces (such as "System Volume Information") are now
  quoted in the generated script, so robocopy reads each as a single name
  instead of several.
- On the File Backup tab, "Add Exclusion..." and "Edit Folder..." both used the
  Alt+E access key. "Add Exclusion..." is now Alt+U so each control has a unique
  mnemonic.

## [0.3.0] - 2026-06-11

### Added
- Choose which days of the week the scheduled backup runs on: tick any mix of
  weekdays (all seven for daily, one for weekly, or a custom set), replacing the
  previous daily-only schedule.

### Changed
- Scheduled backups are now off by default on a fresh install; you opt in by
  ticking "Run a scheduled backup", so a new user is never given a scheduled task
  they did not ask for.
- Renamed the Windows scheduled task from "Daily GUARD Backup" to "GUARD Backup"
  (the schedule is no longer always daily). The old task is removed automatically
  when you next save, so upgraders are not left running two backups.
- Consolidated scheduling into Save Settings as the single place to apply it:
  removed the separate "Create/Update Task" and "Remove Task" buttons. Saving now
  registers the task when "Run a scheduled backup" is ticked and removes it when
  not, so there is one clear save action.
- The day and time controls grey out while the schedule is off.
- App Inventory filter now searches publisher, app type (winget / store / manual),
  and package ID in addition to name. The filter box is also slightly wider to
  accommodate the expanded placeholder text.
- Clarified the difference between unticking a backup folder (skip it for now,
  keep it in the list) and Remove Folder (forget it entirely): the Remove Folder
  button gained a tooltip explaining the untick alternative, and its confirm
  dialog now says the removal is permanent and points to unticking for a
  temporary skip.
- The version shown in the About dialog is now derived from the release git tag
  at build time (via MinVer) instead of a hand-edited constant, so it can never
  lag behind a release again. Dev builds between releases show a pre-release
  label (e.g. 0.3.0-alpha.0.6) so they are distinguishable from tagged releases.
- Pressing Remove Folder with no folder highlighted now shows input-neutral
  guidance ("Highlight the folder you want to remove from the folder list")
  instead of keyboard-only Tab/arrow instructions.

### Fixed
- Screen readers announced the backup folder list's second column as "subfolder"
  while the visible header read "Destination subfolder"; the row's spoken name now
  matches the header.
- The About dialog reported version 0.1 instead of the actual release version.
- Pressing an access key that opens a dialog (e.g. Alt+R, Remove Folder) while
  another dialog such as About was already open crashed the app; handling added
  to ensure only one dialog is opened at a time.
- The saved-status line no longer re-announces itself to screen readers on every
  checkbox or field change when its text has not actually changed.
- Exclude lists with more than one entry were silently truncated to the first
  line across a save/reload. A WinUI multi-line text box separates lines with a
  bare carriage return, which the settings writer left unescaped in
  `backup-settings.ini`; on reload the value was split on that carriage return and
  only the first line survived (and the generated `guard-backup.cmd` likewise
  received a single malformed exclude). All newline forms are now normalized, so
  every excluded folder/file name is preserved and written as its own exclude.

## [0.2.0] - 2026-06-04

### Changed
- The shipping release now ships as `GUARD.zip` containing a `GUARD\` folder
  (the exe plus `README.md`) instead of a bare `GUARD.exe`. Extracting it gives a
  self-contained app folder, so GUARD's working files stay together next to the
  exe instead of scattering into wherever a loose exe was saved (e.g. Downloads).
  Bundling `README.md` also makes the in-app Help button open it offline.
- Renamed the backup folder list's "Subfolder" column to "Destination subfolder"
  (and matched the Add Folder dialog's field and help text), making clear it names
  the folder created under the backup destination root rather than a source path.
- Replaced the free-type schedule time box with a native WinUI `TimePicker`:
  hour and minute are separate spin columns you arrow through or pick from a
  flyout, instead of typing `HH:mm` by hand. It follows the system 12-/24-hour
  clock setting (the stored schedule stays 24-hour `HH:mm` regardless).
- Help now uses the conventional `F1` shortcut instead of an `Alt+H` mnemonic,
  and the About button no longer carries `Alt+A`; that frees `Alt+A` for the
  Add Folder button (previously the unintuitive `Alt+F`), which in turn leaves
  `Alt+F` to the App Inventory filter box.
- The About dialog's project-page button now reads "Open Project Page" with its
  visible label as its accessible name (the longer description moved to a hover
  tooltip), instead of a verbose `AutomationProperties.Name` that did not match
  the visible text.
- Documented the actual minimum OS as Windows 10 version 1809 (build 17763),
  matching the Windows App SDK floor, instead of "Windows 10 or 11".

### Added
- Tooltips on the exclude-folder and exclude-file fields noting that wildcards
  (`*`, `?`) are supported, e.g. `*.iso`.

### Fixed
- The root-level build scripts (`publish-aot.cmd`, `publish-singlefile.cmd`) were
  bundled into the shipping single-file `GUARD.exe` and extracted to the runtime
  self-extraction cache, because the SDK globbed them as default project items.
  They are now excluded from the build, so they no longer ship inside the app.
- Several more stray repo and source files (`README.md`, `CHANGELOG.md`,
  `CLAUDE.md`, `LICENSE`, `.gitignore`, `app.manifest`, and the `.xaml` source)
  were likewise bundled into the single-file `GUARD.exe` and extracted to the
  self-extraction cache. The doc/metadata files are now excluded from the build;
  the `.xaml` and `app.manifest` stay as build inputs but their redundant loose
  copies are stripped from the publish output, so none of them ship inside the app.
- The shipping single-file build wrote its working files (`backup-settings.ini`,
  `guard-backup.cmd`, `Logs\`) into a temporary self-extraction cache instead of
  next to the exe, because it derived paths from `AppContext.BaseDirectory`, which
  points at the extraction directory under single-file self-extraction. Paths now
  derive from `Environment.ProcessPath`, so they land next to the exe as intended.
- The window now centres in the display work area on open instead of using the
  OS cascade position, so a tall window no longer appears with its title bar
  partway down the screen and its bottom running off the display.
- The App Inventory "Filter" label now sits inline to the left of the filter
  box, on the same row as the toolbar buttons, instead of floating above it.

## [0.1.0] - 2026-06-03

First public pre-release. WinUI 3 edition (.NET 10 + Windows App SDK 1.8),
shipping as a single self-contained `GUARD.exe`.

### Added
- File backup to any local, external, or network destination, built on Robocopy.
- Additive and Mirror copy modes.
- Folder- and file-name exclusions, one per line.
- Preview (dry-run) mode, per-folder progress, and a saved log of the last run.
- Optional daily scheduled task (`Daily GUARD Backup`) via a standalone,
  self-contained `guard-backup.cmd`.
- App Inventory: registry scan with winget enrichment, exportable to JSON.
- Dark / light Mica theming that follows the Windows setting.
- Screen-reader-first design: real check-box rows, single-tab-stop lists,
  arrow-key navigation, and re-entry focus memory.

[Unreleased]: https://github.com/PlanetLinux98/guard/compare/v0.5.3...HEAD
[0.5.3]: https://github.com/PlanetLinux98/guard/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/PlanetLinux98/guard/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/PlanetLinux98/guard/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/PlanetLinux98/guard/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/PlanetLinux98/guard/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/PlanetLinux98/guard/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/PlanetLinux98/guard/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/PlanetLinux98/guard/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/PlanetLinux98/guard/releases/tag/v0.1.0
