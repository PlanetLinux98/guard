<!-- Decorative: the heading text already says GUARD, so the logo carries an
     empty alt and screen readers skip straight to the title. -->
# <img src="Assets/guard-icon-256.png" width="48" alt=""> GUARD

[User Manual](USER_GUIDE.md) · [Releases](https://github.com/PlanetLinux98/guard/releases) · [Changelog](CHANGELOG.md)

A portable, fully accessible backup and data-protection utility for Windows.
GUARD backs up your chosen folders to any destination (a local drive, an
external disk, or a network share), captures a full bare-metal image of the whole
PC, and keeps an inventory of your installed apps so they are easy to reinstall,
and their settings to restore, after a clean OS install. It carries no installer and no dependencies to track down: the shipping
release is a small `GUARD.zip` you extract to a self-contained `GUARD\` folder,
then run the `GUARD.exe` inside.

GUARD is also on **winget**: `winget install --id PlanetLinux98.GUARD` (or just
`winget install GUARD`). New releases usually appear in winget within a couple of
days of the GitHub Release.

> **New to GUARD?** The [User Manual](USER_GUIDE.md) covers setup and every
> feature in detail. It also ships inside the release zip, and the in-app Help
> item (F1) opens it.

The goal is to become the ultimate portable data-protection toolkit. More
functionality to come!

> Pre-release: Expect rough edges and many future improvements.

Built for first-class screen-reader support and native dark-mode theming, GUARD
targets **.NET 10 + Windows App SDK 2.2.0** (WinUI 3).

## Current Features

- **File backup** to any folder: local, external, or a network share.
- **Additive** or **Mirror** copy modes (built on Robocopy): Additive only adds
  new and changed files and never deletes anything at the destination; Mirror
  makes the destination match the source exactly.
- **Exclusion presets** (temporary files, system clutter, developer folders,
  caches and disc images) plus a guided dialog for custom folder, extension, or
  pattern exclusions, so common cases need no wildcard typing.
- **Preview (dry-run)** mode, per-folder progress, a plain-language run summary,
  and a saved log of the last run.
- **File Restore**: copies files from a backup back into the folders they came
  from, listing what is actually in the backup (so it works on a PC that has
  never been set up) and offering a dated version to restore from. The default
  puts back what is missing and never replaces newer work; a second mode
  replaces files whatever their date, after showing what would change. Neither
  ever deletes, and a backup and a restore can never run at the same time.
- **Protection Status page**: whether your files, your whole PC and your
  programs are actually protected right now, judged from the run logs and the
  destinations themselves rather than from your settings.
- **Optional dated backup versions**: keep a configurable number of dated copies
  instead of just the latest.
- **Scheduling**: run on any mix of weekdays at a set time, and/or
  automatically when the backup destination becomes available, all unattended
  whether or not GUARD is open.
- **App Management**: lists your installed apps from the Windows registry, marks
  the ones winget can reinstall, and exports the list (optionally bundled with
  the apps' settings folders) as a dated export. Importing a saved list back
  onto a fresh PC reinstalls the apps and can restore their settings. Update
  All Apps upgrades everything winget knows in one elevated pass, and GUARD can
  install winget itself on editions that ship without it (LTSC, Server, no
  Store).
- **System Image** (whole-PC, bare-metal): captures a full image of Windows and
  all your programs and settings with the built-in `wbadmin` engine, to a local or
  external disk (keeping several past images) or a network share, on demand or on a
  Weekly/Monthly/Daily schedule. A Create Recovery Media wizard builds a bootable
  USB to restore from, and Restore Instructions walk through the offline recovery
  (including reaching a network share, which the recovery environment addresses by
  IP address rather than name). Needs a Windows edition that includes `wbadmin`
  (so not Home), and refuses a destination on the Windows drive itself.
- **Stop buttons** for both long-running jobs and a persistent status bar
  surfacing progress and backup health (how and when the last run ended, and
  whether a scheduled run is overdue).
- **Built-in updater**: a daily check against GitHub Releases offers new
  versions (with readable release notes), verifies the download against the
  release's checksum, and can optionally install updates on exit; Windows
  notifications report unattended backup outcomes (failures by default). A copy
  installed with winget updates through `winget upgrade` instead, since winget
  owns that folder.
- **Dark / light Mica theming** that follows the Windows setting automatically.
- **Screen-reader-first design** is the reason this app exists; accessibility is
  woven through every control. See the [User Manual](USER_GUIDE.md) for details.

## Requirements

- Windows 10 version 1809 (build 17763) or later, including Windows 11. Note
  that Windows 10 reached end of support on October 14, 2025; GUARD still runs
  there, but it is outside Microsoft's support window for the Windows App SDK.
- **Nothing to install to run it.** The release zip's `GUARD\` folder is
  self-contained: the .NET 10 runtime and Windows App SDK are bundled in it.
- winget (optional) for automatic app reinstalls. Without it, the app list is
  still read from the registry and can be exported for reference, and GUARD
  offers to install winget itself where it is missing.
- To **build from source** you need the .NET 10 SDK, and more for the shipping
  build (see [Build prerequisites](#build-prerequisites)).

## Building

### Build prerequisites

A development build needs only the first row. The others are needed solely by
the shipping NativeAOT build.

| | Needed for | Notes |
|---|---|---|
| **.NET 10 SDK** | Everything | GUARD targets `net10.0-windows10.0.19041.0`. The Windows App SDK arrives through NuGet, and the build is self-contained, so there is no separate runtime to install |
| **VS 2022 Build Tools**, "Desktop development with C++" | `publish-release.cmd`, `publish-aot.cmd` | The NativeAOT link step needs `link.exe` plus the MSVC and Windows SDK libraries. Keep the default install location: the scripts call `vcvars64.bat` there by absolute path, and put the VS Installer folder on PATH so the AOT target can find `vswhere.exe` by bare name |
| **Python 3** with `markdown` | `publish-release.cmd` | Renders `USER_GUIDE.md` into the `USER_GUIDE.html` the release ships, and fails the build on a broken internal anchor. `pip install markdown` |
| **Python** `resvg_py` and `pillow` | Only regenerating the app icon | `Assets\make-icon.py` rebuilds the committed `GUARD.ico` from the SVG masters. Not part of any build |

Clone with tags (a plain `git clone`, not a shallow one): the version is computed
from git tags by MinVer, so a clone without them produces wrongly versioned
builds.

Run the tests with `dotnet test Tests\GUARD.Tests.csproj`; they need no tooling
beyond the SDK.

### Shipping build: NativeAOT release zip (recommended)

```
publish-release.cmd
```

Builds a self-contained **NativeAOT** GUARD, stages the publish folder into a
`GUARD\` folder alongside `USER_GUIDE.html` (the manual, rendered from
`USER_GUIDE.md` by `make-user-guide.py`; needs Python 3 with
`pip install markdown`), and zips it to `GUARD.zip` in the project root. NativeAOT cannot be a single `.exe` (WinUI 3 / Windows App SDK ship
native DLLs that cannot be merged into the AOT binary), so the release is the
whole folder - which GUARD already ships as. Being a portable app, GUARD writes
its working files (`backup-settings.ini`, `guard-backup.cmd`, `Logs\`) next to
`GUARD.exe` in that folder, so shipping a folder keeps everything together
instead of littering the folder the zip was downloaded to. (Installed with
winget those files live in `%LOCALAPPDATA%\GUARD` instead: winget deletes its
package folder on every upgrade, so nothing GUARD needs can be kept there.) Requires the VS 2022
Build Tools "Desktop development with C++" workload for the AOT link step (see
[NativeAOT](#nativeaot)).

### Build and run (development)

```
dotnet build -r win-x64 -c Debug
```

Development builds are plain JIT - debuggable and buildable with just the .NET
SDK (no C++ toolchain). Only the shipping build is AOT.

### Quick AOT build (no packaging)

```
publish-aot.cmd
```

Publishes the AOT binary to `bin\...\publish\` without staging or zipping - handy
for testing an AOT build quickly.

### R2R fallback (no C++ toolchain)

```
dotnet publish -r win-x64 -c Release
```

A self-contained ReadyToRun folder build (~285 files, the exe needs the DLLs
beside it) in `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`. Not the
shipped artifact, but a working build for anyone without the C++ tools AOT needs.

## Project layout

| Path | Purpose |
|---|---|
| `Models/` | FolderPair, AppEntry, Settings, AppListFile, exclude and app-settings models (+ System.Text.Json source-gen context) |
| `Services/` | Settings I/O, backup-script generation, scheduled tasks, winget + registry scan, app-settings export/restore, system-image + recovery-media scripting, the updater, JSON I/O, process helpers |
| `MainWindow.xaml(.cs)` | All five pages and all wiring (code-behind split into per-page partial files) |
| `Views/` | The ContentDialogs (folder, exclude, about, app-import, app-settings export/restore, file-restore, recovery-media, restore-help, update, winget-install) and the status-bar host |
| `Tests/` | Linked-source unit tests (`GUARD.Tests.csproj`, not shipped) |
| `Assets/` | App icon: SVG masters, `make-icon.py`, and the committed `GUARD.ico` + 256 px PNG |
| `publish-release.cmd` | Build the shipping NativeAOT `GUARD` and stage it into `GUARD.zip` |
| `publish-aot.cmd` | Quick NativeAOT build, no packaging (see [NativeAOT](#nativeaot)) |
| `GUARD\`, `GUARD.zip` | The staged release folder and its zip (project root; not source) |
| `USER_GUIDE.md` | The end-user manual; ships in the zip as `USER_GUIDE.html`, opened by Help (F1) |
| `make-user-guide.py` | Renders the manual to the `USER_GUIDE.html` the zip ships (run by `publish-release.cmd`) |

## Usage

GUARD has five pages: **Protection Status**, **File Backup**, **System Image**,
**App Management**, and **Settings** (update options, theme, startup page, and removing GUARD's
scheduled tasks before you uninstall). For a full,
step-by-step walkthrough of every control and workflow, see the
[User Manual](USER_GUIDE.md) (or press F1 in the app).

## NativeAOT

`publish-aot.cmd` builds a true native binary (`-p:PublishAot=true`) inside the
VC x64 developer environment. On Windows App SDK **2.2.0 + .NET 10** it now
launches and renders every data-templated list correctly.

The long-standing startup fail-fast (0xc000027b in Microsoft.UI.Xaml.dll, the
moment a data-templated list bound its `ItemsSource`) had a concrete, two-part
cause that was invisible at the default CsWinRT warning level:

1. **`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`** - CsWinRT must emit `unsafe`
   marshalling stubs for the generic WinRT collection interfaces an
   `ObservableCollection<T>` / `List<T>` of an app type implements when it crosses
   the ABI as `ItemsControl.ItemsSource`. Without it the stubs are silently
   omitted and `set_ItemsSource` throws `E_INVALIDARG` ("Value does not fall
   within the expected range") - the crash (CsWinRT1030).
2. **`partial` on the bound `INotifyPropertyChanged` models** (FolderPair,
   AppEntry, AppSettingsCandidate, AppSettingsRestoreCandidate) so CsWinRT can
   generate their `WinRTExposedType` vtable (CsWinRT1028).

Build with `-p:CsWinRTAotWarningLevel=2` to surface these (the collection
warnings are Level 2; the default level only shows Level 1). The earlier
dotnet/runtime#115881 attribution was a red herring - that was a .NET 10
*preview* interop regression, since fixed, and not AOT-specific.

NativeAOT is the **shipping build** (`publish-release.cmd`). It cannot be a single
`.exe` (WinUI 3 / Windows App SDK native DLLs cannot be merged), so the release is
the whole publish folder, zipped - which GUARD already ships as. Development builds
stay JIT for fast, debuggable iteration without the C++ toolchain.

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
