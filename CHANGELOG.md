# Changelog

All notable changes to GUARD are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While GUARD is in the `0.x` series, behavior may change between minor versions.

## [Unreleased]

## [0.3.0] - 2026-06-11

### Added
- Choose which days of the week the scheduled backup runs on: tick any mix of
  weekdays (all seven for daily, one for weekly, or a custom set), replacing the
  previous daily-only schedule.
- "Edit Folder..." button on the File Backup tab: change the source path or
  destination subfolder of an existing folder pair in place, instead of removing
  and re-adding it. The edit applies to the folder row that last held focus,
  matching how Remove Folder picks its target.
- A results count next to the App Inventory filter box (e.g. "42 of 187 apps"),
  so you can tell how many apps match as you type. It is announced to screen
  readers only while typing in the filter box, and only when the count actually
  changes.

### Changed
- The output consoles on both tabs now scroll automatically to the newest line
  while a backup or reinstall is running, without moving keyboard focus.
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
