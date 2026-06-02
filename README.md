# GUARD

A portable backup and data protection utility for Windows. GUARD backs up your
chosen folders to any destination (a local drive, an external disk or a network
share) and can keep a list of your installed apps so they are easy to reinstall
after a clean OS install.

> Version 0.1 (pre-release). Expect rough edges, and many future improvements.

## Current Features

- **File backup** to any folder: local, external or a network share.
- **Additive** or **Mirror** copy modes (built on Robocopy).
- Per-folder progress, a preview (dry-run) mode and a saved log.
- Optional **daily scheduled task** so backups run unattended.
- **App Inventory**: lists installed apps from the Windows registry, marks the
  ones winget can reinstall, and exports the list as plain JSON.
- Light/dark theming that follows the Windows setting.
- Built for screen-reader use (NVDA/JAWS): every control has access keys and the save status
  line announces itself as a live region.

## Planned Features & Improvements

- **UX improvements** such as better window sizing and inclusion of a menu bar.
- More backup scheduling options (e.g. hourly, weekly, etc.)
- More intuitive progress with a more granular progress bar and cleaner output field text.
- New features planned, such as full system images and application data export/import

## Requirements

- Windows 10 or 11 (earlier versions haven’t been tested).
- .NET Framework 4.x (ships with Windows; no separate SDK needed).
- winget (optional) for automatic app reinstalls. Without it, the app list is
  still read from the registry and can be exported for reference.

## Building

GUARD is a single C# source file compiled by the in-box .NET Framework compiler.
No SDK or project file is required. From this folder:

```
build.cmd
```

Or run the compiler directly (see the command at the top of `Guard.cs`).

## Usage

<!-- PLACEHOLDER - TODO: walk through the File Backup tab, the App Inventory tab, and the
     scheduled task. Add a screenshot. -->

### Exported app list

The App Inventory export is plain, indented JSON. You can open it in any text
editor to read your installed-app list by hand. Each entry records the app name,
version, publisher, and (where known) its winget package id, so you can also run
`winget install --id <id>` yourself.

## Licence

GUARD is released under the MIT License. See the [LICENSE](LICENSE) file for the
full text.

---

*GUARD is developed by [PlanetLinux98](https://github.com/PlanetLinux98/guard).*
