# Changelog

All notable changes to GUARD are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While GUARD is in the `0.x` series, behavior may change between minor versions.

## [Unreleased]

### Added
- Stop buttons for both long-running jobs: "Stop Backup" on the File Backup tab
  cancels a running backup (the whole cmd/robocopy process tree is stopped), and
  "Stop Reinstall" on the App Inventory tab stops the winget reinstall loop after
  the current app (apps already installed stay installed). Each button sits with
  its tab's other actions and is enabled only while its job is running, and the
  output box and progress line report the cancellation instead of going silent.
  Keyboard focus follows the job: starting one moves focus to its Stop button,
  and when it ends focus returns to the button that started it, with the
  end-of-job summary or cancellation message spoken by screen readers.
- A persistent status bar at the bottom of the window. It shows the active
  tab's status (the settings saved/unsaved line on File Backup, the scan or
  import summary on App Inventory) and, while a backup or reinstall is running,
  a compact progress bar with the current action, so progress stays visible
  even when the in-tab progress area is scrolled away or the other tab is
  active. The status bar is the screen-reader live region for status changes,
  and both the File Backup mid-page status line and the App Inventory scan
  summary moved into it; it is exposed to assistive tech as a real status bar,
  so a screen reader's read-status-bar command (NVDA+End) finds it.
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
- A results count next to the App Inventory filter box (e.g. "42 of 187 apps"),
  so you can tell how many apps match as you type. It is announced to screen
  readers only while typing in the filter box, and only when the count actually
  changes.

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

### Changed
- The output consoles on both tabs now scroll automatically to the newest line
  while a backup or reinstall is running, without moving keyboard focus.
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

### Fixed
- Screen readers announced the static label ("Inventory status" / "Settings
  status") instead of the actual message whenever a status line updated, e.g.
  after the app scan finished. The status text itself is now announced.
- Exclude names containing spaces (such as "System Volume Information") are now
  quoted in the generated script, so robocopy reads each as a single name
  instead of several.

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

[Unreleased]: https://github.com/PlanetLinux98/guard/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/PlanetLinux98/guard/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/PlanetLinux98/guard/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/PlanetLinux98/guard/releases/tag/v0.1.0
