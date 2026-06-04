# GUARD

A portable, fully accessible backup and data-protection utility for Windows.
GUARD lets you back up your chosen folders to any destination (a local drive, an
external disk, or a network share) and keep an inventory of your installed apps
so they are easy to reinstall after a clean OS install. It carries no installer
and no dependencies to track down: the shipping release is a small `GUARD.zip`
you extract to a self-contained `GUARD\` folder, then run the `GUARD.exe` inside.

The goal is to become the ultimate portable data-protection toolkit. More
functionality to come!

> Pre-release: Expect rough edges and many future improvements.

This is the **WinUI 3** edition, a ground-up rewrite of the original WPF version
(now retired) built for far better screen-reader support and native dark-mode
theming. It targets **.NET 10 + Windows App SDK 1.8**.

## Current Features

- **File backup** to any folder: local, external, or a network share.
- **Additive** or **Mirror** copy modes (built on Robocopy): Additive only adds
  new and changed files and never deletes anything at the destination; Mirror
  makes the destination match the source exactly.
- **Folder- and file-name exclusions**, one per line, so you can skip caches,
  temp folders, or specific file types.
- **Preview (dry-run)** mode that shows what a backup would do without changing
  anything, per-folder progress, and a saved log of the last run.
- Optional **daily scheduled task** so backups run unattended at a time you set.
- **App Inventory**: lists your installed apps from the Windows registry, marks
  the ones winget can reinstall, and exports the list as plain JSON.
- **Dark / light Mica theming** that follows the Windows setting automatically.
- **Screen-reader-first design** (see [Accessibility](#accessibility) below)

## Planned Features & Improvements

- A menu bar and further UX polish.
- More backup scheduling options (e.g. hourly, weekly).
- More granular progress reporting and cleaner output text.
- New capabilities such as full system images and application-data
  export / import.

## Requirements

- Windows 10 version 1809 (build 17763) or later, including Windows 11. Note
  that Windows 10 reached end of support on October 14, 2025; GUARD still runs
  there, but it is outside Microsoft's support window for the Windows App SDK.
- **Nothing to install to run it.** The `GUARD.exe` inside the release zip is
  self-contained: the .NET 10 runtime and Windows App SDK are bundled inside it.
- winget (optional) for automatic app reinstalls. Without it, the app list is
  still read from the registry and can be exported for reference.
- To **build from source** you need the .NET 10 SDK (see [Building](#building)).

## Building

### Shipping build: single-file GUARD.exe in a release zip (recommended)

```
publish-singlefile.cmd
```

Produces one ~88 MB `GUARD.exe` (self-contained, compressed, ReadyToRun), staged
inside a `GUARD\` folder alongside `README.md` and zipped to `GUARD.zip` in the
project root. The bundled runtime extracts once to a per-user temp cache and is
reused on later launches (it does not scatter DLLs beside the exe). Being a
portable app, GUARD writes its working files (`backup-settings.ini`,
`guard-backup.cmd`, `Logs\`) into the folder the exe sits in, so shipping it
inside a folder keeps everything together instead of littering the folder the zip
was downloaded to.

### Build and run (development)

```
dotnet build -r win-x64 -c Debug
```

### Alternative: folder publish (not single-file)

```
dotnet publish -r win-x64 -c Release
```

Output folder: `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`
(self-contained; ~285 files, the exe needs the DLLs beside it).

## Project layout

| Path | Purpose |
|---|---|
| `Models/` | FolderPair, AppEntry, Settings, AppListFile (+ System.Text.Json source-gen context) |
| `Services/` | Settings I/O, backup-script generation, scheduled tasks, winget + registry scan, JSON I/O, process helpers |
| `MainWindow.xaml(.cs)` | Both tabs and all wiring |
| `Views/` | FolderDialog, AboutDialog (ContentDialogs) |
| `publish-singlefile.cmd` | Build the shipping `GUARD.exe` and stage it into `GUARD.zip` |
| `publish-aot.cmd` | Opt-in NativeAOT publish (see note below) |
| `GUARD\`, `GUARD.zip` | The staged release folder and its zip (project root; not source) |

## Usage

GUARD has two tabs.

**File Backup.** Add one or more source/destination folder pairs, choose
Additive or Mirror, optionally list folder and file names to exclude, and set a
daily run time if you want it scheduled. **Preview** shows what the backup would
do without touching anything; **Save Settings** writes `backup-settings.ini` and
a standalone `guard-backup.cmd`(which can be run independently of the main app),
and (if scheduling is enabled) registers the Windows scheduled task `Daily GUARD
Backup`. Because the generated script is self-contained, your backups keep running
on schedule whether or not GUARD itself is open.

**App Inventory.** Scans the Windows uninstall registry for your installed apps
and marks the ones winget can reinstall. You can export the full list to JSON and
use it to reinstall the winget-capable apps after an OS reinstall.

### Exported app list

The App Inventory export is plain, indented JSON you can open in any text editor.
Each entry records the app name, version, publisher, and (where known) its winget
package id, so you can also run `winget install --id <id>` yourself.

## Accessibility

Accessibility is the reason this app exists. Some current accessibility-related
efforts include the folder and app lists being built from real check boxes rather
than a grid or list view, so a screen reader announces each row's own checked / 
unchecked state with no selection-versus-check confusion. Within a list:

- **Tab treats the whole list as one stop** - Tab enters the list once and the
  next Tab leaves it, instead of stepping through every row.
- **Arrow keys move between rows**, and **Space toggles** the focused check box.
- **Focus is remembered** - tabbing back into a list returns you to the row you
  were last on, not the top.

## NativeAOT (not currently usable)

`publish-aot.cmd` builds a true native binary (`-p:PublishAot=true`) inside the
VC x64 developer environment. It compiles and links, but the resulting exe
**crashes at startup** (0xc000027b in Microsoft.UI.Xaml.dll) the moment a
data-templated list renders. This reproduces under both .NET 9 and .NET 10 with
Windows App SDK 1.8: it is believed to be a current WinUI-3-under-AOT XAML/binding
limitation (see microsoft/WindowsAppSDK discussion #3856). The shipping build
is therefore ReadyToRun, not AOT. Will revisit AOT in the future, but am open to
development assistance in this regard.

## Contributing & branching

GUARD uses a simple **trunk-based** workflow:

- **`main` is always releasable.** Every commit on `main` should build and run.
  Tagged commits on `main` are the releases.
- **Work happens on short-lived branches off `main`**, one per change, named by
  type: `feature/<short-name>`, `fix/<short-name>`, `chore/<short-name>`,
  `docs/<short-name>`. Open a pull request into `main`, then delete the branch
  after it merges. Branches live hours-to-days, not weeks.
- **Releases are git tags** using [SemVer](https://semver.org/) with a `v`
  prefix (`v0.1.0`). Each tag gets a [GitHub Release](https://github.com/PlanetLinux98/guard/releases)
  with the built `GUARD.zip` attached as an asset (the binary is gitignored, so
  the Release is where it ships).
- **Every change updates [CHANGELOG.md](CHANGELOG.md)** under `[Unreleased]`;
  cutting a release moves those entries under the new version heading.

## Licence

GUARD is released under the MIT License. See the [LICENSE](LICENSE) file for the
full text.

---

*GUARD is developed by [PlanetLinux98](https://github.com/PlanetLinux98/guard).*
