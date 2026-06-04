# Changelog

All notable changes to GUARD are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While GUARD is in the `0.x` series, behavior may change between minor versions.

## [Unreleased]

### Changed
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

[Unreleased]: https://github.com/PlanetLinux98/guard/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/PlanetLinux98/guard/releases/tag/v0.1.0
