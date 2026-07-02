using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GuardWui3.Services;

// Builds a bootable Windows installation USB the user can boot to restore a
// system image (WinRE "System Image Recovery"). Everything uses in-box tools
// (diskpart, robocopy, DISM, Mount-DiskImage) driven through an elevated
// PowerShell script - no Rufus, no third-party, no new NuGet.
//
// The destructive build runs entirely elevated and re-checks the target disk is
// a removable USB immediately before wiping it. The ISO is validated (mounted,
// \sources present) BEFORE the disk is touched, so a bad ISO never costs the
// user their USB.
public static class RecoveryMedia
{
    public sealed record UsbDisk(int Number, string Model, long SizeBytes);

    // The USB only boots WinRE and launches System Image Recovery; it does not
    // install Windows, so the EDITION is irrelevant - only architecture and major
    // version must match the image being restored.
    public static string DetectArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "ARM64",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => RuntimeInformation.OSArchitecture.ToString(),
    };

    // .NET's Environment.OSVersion uses RtlGetVersion, so the build number is the
    // true one (not shimmed by the app manifest). 22000+ is Windows 11.
    public static int DetectWindowsMajor() => Environment.OSVersion.Version.Build >= 22000 ? 11 : 10;

    // Microsoft gates ISO downloads behind a session/region token with no stable
    // scriptable endpoint, so GUARD opens the official download page rather than
    // scraping a link that would break. The user downloads, then points the wizard
    // at the .iso.
    public static string DownloadPageUrl(int major) =>
        major >= 11 ? "https://www.microsoft.com/software-download/windows11"
                    : "https://www.microsoft.com/software-download/windows10";

    // Removable USB disks only; fixed, system and boot disks are never listed, so
    // the wizard can't offer the user a way to wipe their own system drive.
    public static Task<List<UsbDisk>> EnumerateRemovableDrivesAsync()
    {
        return Task.Run(() =>
        {
            var list = new List<UsbDisk>();
            try
            {
                string ps = "Get-Disk | Where-Object { $_.BusType -eq 'USB' -and -not $_.IsSystem -and -not $_.IsBoot } | " +
                            "ForEach-Object { \"$($_.Number)|$($_.FriendlyName)|$($_.Size)\" }";
                string outp = ProcessRunner.RunPowerShellCapture(ps);
                foreach (var raw in outp.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var parts = line.Split('|');
                    if (parts.Length >= 3 && int.TryParse(parts[0].Trim(), out int n)
                        && long.TryParse(parts[2].Trim(), out long sz))
                        list.Add(new UsbDisk(n, parts[1].Trim(), sz));
                }
            }
            catch { }
            return list;
        });
    }

    // The elevated build script. Order matters for safety: validate the ISO first,
    // re-verify the disk is removable USB second, and only then wipe it. The big
    // install.wim is split into .swm chunks so it fits FAT32's 4 GB file cap (the
    // step users get wrong by hand); install.esd (already under 4 GB) is copied as
    // is. UEFI boots from the FAT32 boot files copied off the ISO; bootsect adds
    // legacy-BIOS boot code best-effort.
    //
    // Progress is written to the log the wizard tails (output can't cross the
    // elevation boundary): "@@PCT@@ n" lines drive the determinate bar, other lines
    // are shown as status. The script checks $cancel at each stage boundary so the
    // wizard can stop it (the elevated process itself can't be killed from the app).
    public static string BuildUsbScript(int diskNumber, string isoPath, string logPath, string cancelPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$disk = " + diskNumber);
        sb.AppendLine("$iso = '" + PsLiteral(isoPath) + "'");
        sb.AppendLine("$log = '" + PsLiteral(logPath) + "'");
        sb.AppendLine("$cancel = '" + PsLiteral(cancelPath) + "'");
        sb.AppendLine("function Log($m){ $m | Out-File -FilePath $log -Append -Encoding UTF8 }");
        sb.AppendLine("function Pct($n){ Log ('@@PCT@@ ' + $n) }");
        sb.AppendLine("function Stop-IfCancelled($iso){ if (Test-Path $cancel) { try { Dismount-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue | Out-Null } catch {}; Log 'CANCELLED'; exit 9 } }");
        // Every anticipated failure below dismounts the ISO and logs a reason,
        // but ErrorActionPreference=Stop makes any UNanticipated error (say the
        // USB disk vanishing mid-script) terminating - without this trap that
        // strands the mounted ISO and leaves the wizard a reasonless failure.
        sb.AppendLine("trap { try { Dismount-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue | Out-Null } catch {}; Log ('ERROR: ' + $_); exit 1 }");
        sb.AppendLine("'' | Out-File -FilePath $log -Encoding UTF8");
        sb.AppendLine("Remove-Item $cancel -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Log ('Recovery media build  ' + (Get-Date))");
        sb.AppendLine("Log ('ISO: ' + $iso)");
        sb.AppendLine("Pct 2");
        // 1. Validate the ISO before touching the disk.
        sb.AppendLine("Log 'Checking the ISO...'");
        sb.AppendLine("if (-not (Test-Path $iso)) { Log 'ERROR: ISO file not found.'; exit 2 }");
        sb.AppendLine("$mount = Mount-DiskImage -ImagePath $iso -PassThru");
        sb.AppendLine("Start-Sleep -Seconds 1");
        sb.AppendLine("$src = ($mount | Get-Volume).DriveLetter");
        sb.AppendLine("if (-not $src) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log 'ERROR: could not mount the ISO.'; exit 3 }");
        sb.AppendLine("$srcRoot = \"${src}:\\\"");
        sb.AppendLine("if (-not (Test-Path ($srcRoot + 'sources')) -or -not (Test-Path ($srcRoot + 'boot'))) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log 'ERROR: this is not a Windows installation ISO (no sources or boot folder).'; exit 4 }");
        sb.AppendLine("Pct 5");
        // 2. Re-verify the target is a removable USB and not the system/boot disk.
        sb.AppendLine("$d = Get-Disk -Number $disk");
        sb.AppendLine("if ($d.BusType -ne 'USB' -or $d.IsSystem -or $d.IsBoot) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log 'ERROR: the chosen disk is not a removable USB disk; aborting for safety.'; exit 5 }");
        sb.AppendLine("Stop-IfCancelled $iso");
        // 3. Wipe to a single active FAT32 primary partition on an MBR disk (boots
        //    both UEFI and legacy BIOS). Cap near 30 GB: Windows cannot format FAT32
        //    above 32 GB, so a larger stick would leave a RAW volume copies fail on.
        sb.AppendLine("$diskMB = [math]::Floor($d.Size / 1MB)");
        sb.AppendLine("$partMB = [math]::Min($diskMB - 50, 30000)");
        sb.AppendLine("if ($partMB -lt 7000) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log 'ERROR: the USB drive is too small; use an 8 GB or larger drive.'; exit 7 }");
        sb.AppendLine("Log ('Formatting disk ' + $disk + ' (' + $d.FriendlyName + ')...')");
        sb.AppendLine("$dpFile = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'guard_dp_' + [Guid]::NewGuid().ToString('N') + '.txt')");
        // Each diskpart line must be its own array element, built with string
        // interpolation. Do NOT write '...' + $x inside @(...): the , / + operator
        // precedence folds the whole array into ONE string, so diskpart received a
        // single 'select disk 3 clean convert mbr ...' line and rejected it as
        // invalid (which is why the disk was never wiped). convert mbr (on the empty
        // disk after clean) makes 'active' and bootsect /nt60 below valid and yields
        // a layout that boots both UEFI and legacy BIOS.
        sb.AppendLine("$dpLines = @(\"select disk $disk\", 'clean', 'convert mbr', \"create partition primary size=$([int]$partMB)\", 'format fs=fat32 quick label=GUARD-WIN', 'active', 'assign')");
        sb.AppendLine("Set-Content -Path $dpFile -Value $dpLines -Encoding ASCII");
        // Capture diskpart's own output (its errors print to stdout) so a failed
        // clean or format is visible in the log instead of masked.
        sb.AppendLine("$dpOut = & diskpart /s $dpFile | Out-String");
        sb.AppendLine("$dpExit = $LASTEXITCODE");
        sb.AppendLine("Remove-Item $dpFile -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("foreach ($l in ($dpOut -split \"`r?`n\")) { $t = $l.Trim(); if ($t) { Log ('  diskpart: ' + $t) } }");
        sb.AppendLine("if ($dpExit -ne 0) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log ('ERROR: preparing the USB failed; diskpart exited ' + $dpExit + ' (see the diskpart lines above).'); exit 6 }");
        // Find the FAT32 volume ON THIS DISK, retrying while the new volume settles.
        // Matching by disk (not 'first partition with a letter') stops a stale
        // leftover partition being mistaken for the fresh one and misreported as a
        // format failure; the format/FS recognition can also lag a few seconds.
        sb.AppendLine("$dst = $null");
        sb.AppendLine("for ($i = 0; $i -lt 12; $i++) {");
        sb.AppendLine("  Start-Sleep -Seconds 1");
        sb.AppendLine("  try { Update-HostStorageCache } catch {}");
        sb.AppendLine("  $v = Get-Partition -DiskNumber $disk -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.FileSystem -eq 'FAT32' } | Select-Object -First 1");
        sb.AppendLine("  if ($v) { $dst = $v.DriveLetter; break }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $dst) {");
        sb.AppendLine("  $got = ((Get-Partition -DiskNumber $disk -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | Get-Volume -ErrorAction SilentlyContinue | ForEach-Object { if ($_.FileSystem) { $_.FileSystem } else { 'RAW' } }) -join ',')");
        sb.AppendLine("  Dismount-DiskImage -ImagePath $iso | Out-Null; Log ('ERROR: the USB drive could not be formatted as FAT32 (got ' + $got + '). The disk may not have been wiped; see the diskpart lines above.'); exit 8");
        sb.AppendLine("}");
        sb.AppendLine("$dstRoot = \"${dst}:\\\"");
        sb.AppendLine("Pct 12");
        sb.AppendLine("Stop-IfCancelled $iso");
        // 4. Copy everything except the big install image. /R:2 /W:5 is ESSENTIAL:
        //    robocopy's default is a million retries at 30s each, so one bad file
        //    would hang the build for days.
        sb.AppendLine("Log 'Copying files (this can take several minutes)...'");
        sb.AppendLine("robocopy \"$srcRoot.\" \"$dstRoot.\" /E /XF install.wim /R:2 /W:5 /NFL /NDL /NJH /NJS /NP | Out-Null");
        sb.AppendLine("if ($LASTEXITCODE -ge 8) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log ('ERROR: copying files to the USB failed (robocopy ' + $LASTEXITCODE + ').'); exit 10 }");
        sb.AppendLine("Pct 65");
        sb.AppendLine("Stop-IfCancelled $iso");
        // 5. Split install.wim onto FAT32 if over 4 GB, else copy as-is.
        sb.AppendLine("$wim = $srcRoot + 'sources\\install.wim'");
        sb.AppendLine("if (Test-Path $wim) {");
        sb.AppendLine("  if (((Get-Item $wim).Length / 1MB) -gt 4000) {");
        sb.AppendLine("    Log 'Splitting the Windows image to fit the USB. This is the slowest step and can take 10 minutes or more on a slow USB drive; the progress bar will not move until it finishes. Do not remove the drive.'");
        sb.AppendLine("    DISM /Split-Image /ImageFile:\"$wim\" /SWMFile:\"$($dstRoot)sources\\install.swm\" /FileSize:4000 | Out-Null");
        sb.AppendLine("    if ($LASTEXITCODE -ne 0) { Dismount-DiskImage -ImagePath $iso | Out-Null; Log ('ERROR: splitting the Windows image failed (DISM ' + $LASTEXITCODE + ').'); exit 11 }");
        sb.AppendLine("  } else {");
        sb.AppendLine("    Log 'Copying the Windows image...'");
        sb.AppendLine("    Copy-Item $wim ($dstRoot + 'sources\\install.wim') -Force");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("Pct 92");
        // 6. Best-effort legacy-BIOS boot code (UEFI already boots the FAT32 files).
        sb.AppendLine("Log 'Finishing up...'");
        sb.AppendLine("$bootsect = $dstRoot + 'boot\\bootsect.exe'");
        sb.AppendLine("if (Test-Path $bootsect) { & $bootsect /nt60 \"${dst}:\" /force | Out-Null }");
        sb.AppendLine("Dismount-DiskImage -ImagePath $iso | Out-Null");
        sb.AppendLine("Pct 100");
        sb.AppendLine("Log 'FINISHED OK'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    // Escape for a single-quoted PowerShell string (double any single quote).
    private static string PsLiteral(string s) => (s ?? "").Replace("'", "''");
}
