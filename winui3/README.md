# GUARD (WinUI 3 edition)

WinUI 3 rewrite of the WPF GUARD backup / app-inventory utility. Unpackaged,
self-contained, .NET 10 + Windows App SDK 1.8. Two tabs (File Backup, App
Inventory), dark/light Mica theming following the OS, and a screen-reader-first
UI (each list row is a real check box, not a selectable grid row).

## Layout

| Path | Purpose |
|---|---|
| `Models/` | FolderPair, AppEntry, Settings, AppListFile (+ System.Text.Json source-gen context) |
| `Services/` | Settings I/O, backup-script generation, scheduled tasks, winget + registry scan, JSON I/O, process helpers |
| `MainWindow.xaml(.cs)` | Both tabs and all wiring |
| `Views/` | FolderDialog, AboutDialog (ContentDialogs) |
| `publish-singlefile.cmd` | Build the shipping single-file `GUARD.exe` |
| `publish-aot.cmd` | Opt-in NativeAOT publish (see note below) |
| `GUARD.exe` | The built single-file app (project root; not source) |

## Shipping build: standalone single-file GUARD.exe (recommended)

```
publish-singlefile.cmd
```

Produces one ~103 MB `GUARD.exe` (self-contained, compressed, ReadyToRun) and
copies it to this `winui3/` folder. It is a genuine standalone file: move it
anywhere and double-click. The bundled runtime
extracts once to a per-user temp cache and is reused on later launches (it does
not scatter DLLs beside the exe). Being a portable app, GUARD writes its working
files (`backup-settings.ini`, `guard-backup.cmd`, `Logs\`) into whatever folder
the exe is run from.

The default window opens at 1040 x 900 effective pixels, DPI-scaled so the full
width of controls is visible without manual resizing on any display scaling.

## Build and run (development)

```
dotnet build -r win-x64 -c Debug
```

## Alternative: folder publish (not single-file)

```
dotnet publish -r win-x64 -c Release
```

Output folder: `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`
(self-contained; ~285 files, the exe needs the DLLs beside it).

## NativeAOT (not currently usable)

`publish-aot.cmd` builds a true native binary (`-p:PublishAot=true`) inside the
VC x64 developer environment. It compiles and links, but the resulting exe
**crashes at startup** (0xc000027b in Microsoft.UI.Xaml.dll) the moment a
data-templated list renders. This reproduces under both .NET 9 and .NET 10 with
Windows App SDK 1.8: it is a current WinUI-3-under-AOT XAML/binding limitation
(see microsoft/WindowsAppSDK discussion #3856), not app code. Revisit when a
later Windows App SDK fixes XAML binding under AOT.

NativeAOT prerequisites (already satisfied on this machine): the
"Desktop development with C++" workload (VS 2022 Build Tools), and the VS
Installer directory on PATH so the link step can find `vswhere.exe`.
