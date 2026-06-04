# Changelog

All notable changes to GUARD are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While GUARD is in the `0.x` series, behavior may change between minor versions.

## [Unreleased]

### Changed
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
