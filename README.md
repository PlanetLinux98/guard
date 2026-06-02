# GUARD

A portable Windows backup and app-inventory utility. GUARD backs up your chosen
folders to any destination (local drive, external disk, or network share) by
generating a standalone Robocopy script and a daily scheduled task, and it
inventories your installed apps so you can export the list and reinstall the
winget-capable ones after an OS reinstall.

This repo contains **two editions** of the app:

| Edition | Folder | Stack | Status |
|---|---|---|---|
| **WinUI 3** | [`winui3/`](winui3/) | .NET 10 + Windows App SDK 1.8 (SDK-style project) | **Primary.** Screen-reader-first, native dark/light Mica theming. |
| WPF | [`wpf/`](wpf/) | .NET Framework 4.x, single `Guard.cs`, in-box `csc.exe` | Original. Zero-dependency, single-file source; no SDK required to build. |

Both are feature-equivalent **at the moment**. The WinUI 3 edition is the one to use going forward
(better accessibility and theming); the WPF edition is kept for its dead-simple,
no-SDK build and as the original reference. WPF version may not be maintained in the future.

## Building

- WinUI 3: `cd winui3 && publish-singlefile.cmd` (see [`winui3/README.md`](winui3/README.md)).
- WPF: `cd wpf && build.cmd` (see [`wpf/`](wpf/)).

## License

MIT - see [LICENSE](LICENSE). Applies to both editions.
