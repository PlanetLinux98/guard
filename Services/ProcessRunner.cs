using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace GuardWui3.Services;

public static class ProcessRunner
{
    // Capture stdout of a console tool (used for `winget list`). Throws when the
    // executable is not on PATH, which the caller treats as "winget missing".
    public static string RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        // Drain both pipes concurrently: reading one to the end while the child
        // blocks on the other pipe's full buffer deadlocks both sides.
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        se.GetAwaiter().GetResult();
        return so.GetAwaiter().GetResult();
    }

    // Runs `winget install` for one id; see RunWinget for the mechanics.
    // ArgumentList (via RunWinget), not a concatenated string: the id comes
    // from an imported app-list.json (hand-editable), so a quote in it must be
    // escaped as data rather than splice extra arguments into the command line.
    public static int RunWingetInstall(string id, Action<string> onLine, CancellationToken ct = default)
        => RunWinget(new[] { "install", "--id", id, "-e", "--silent",
               "--accept-package-agreements", "--accept-source-agreements" }, onLine, ct);

    // Runs winget with the given arguments, streaming combined output via
    // onLine; returns the exit code. Cancelling the token kills the whole
    // winget process tree, making WaitForExit return. Killing inside this
    // method (not handing the Process to the caller) keeps the kill within the
    // using scope, so a cancel can't race disposal. Kill throws if the process
    // already exited - benign, so swallowed.
    public static int RunWinget(IEnumerable<string> args, Action<string> onLine, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data + "\r\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data + "\r\n"); };
        p.Start();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    public static string RunPowerShellCapture(string script)
    {
        // -EncodedCommand, not a quote-escaped -Command: the script stays data
        // whatever quotes, carets or percent signs it contains, removing the
        // whole cmd-vs-PowerShell quoting hazard class.
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        // Concurrent drain; see RunCapture for the deadlock reasoning.
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        se.GetAwaiter().GetResult();
        return so.GetAwaiter().GetResult();
    }

    // Run a PowerShell script FILE non-elevated, returning the exit code with
    // stdout and stderr combined. Unlike RunPowerShellCapture the caller can
    // tell a failed run from a quiet success, and stderr carries the error
    // text (Add-AppxPackage reports deployment failures there).
    public static int RunPowerShellFileCapture(string scriptPath, out string output)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        // Concurrent drain; see RunCapture for the deadlock reasoning.
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        string e = se.GetAwaiter().GetResult().Trim();
        string o = so.GetAwaiter().GetResult().Trim();
        output = o.Length > 0 && e.Length > 0 ? o + "\n" + e : o + e;
        return p.ExitCode;
    }

    // Sentinels returned by RunPowerShellElevatedCode when the elevated process
    // never ran (so they are distinguishable from a real winget/tool exit code;
    // no real HRESULT lands this close to int.MinValue).
    public const int ElevationDeclined = int.MinValue;
    public const int ElevationLaunchFailed = int.MinValue + 1;

    // Run a PowerShell script ELEVATED (UAC prompt) and return the script's exit
    // code. Output can't cross the elevation boundary, so a script that needs its
    // output redirects to a log the caller tails; the exit code still crosses
    // (the parent reads the child's ExitCode even through runas). Returns an
    // Elevation* sentinel and sets error when the process could not start.
    public static int RunPowerShellElevatedCode(string script, out string? error)
    {
        error = null;
        string ps1 = Path.Combine(Path.GetTempPath(), "guard_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            // Encoding.UTF8 (not the 2-arg WriteAllText overload, which omits
            // the BOM) so Windows PowerShell reads the file as UTF-8 instead of
            // guessing the system codepage; without a BOM, a non-ASCII path
            // (e.g. an accented Windows username) gets misread and corrupts
            // the script's quoted arguments.
            File.WriteAllText(ps1, script, Encoding.UTF8);
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        // 1223 (ERROR_CANCELLED) is the actual UAC-decline code; any other
        // Win32Exception (e.g. a missing/corrupted powershell.exe) is a real
        // launch failure and must not be mislabeled as a declined prompt.
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "was cancelled - Administrator approval was declined.";
            return ElevationDeclined;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return ElevationLaunchFailed;
        }
        finally
        {
            try { File.Delete(ps1); } catch { }
        }
    }

    // As above, reduced to success/failure for callers that only run a task and
    // check whether it completed (system image, recovery media, wbadmin stop).
    public static bool RunPowerShellElevated(string script, out string? error)
    {
        int code = RunPowerShellElevatedCode(script, out error);
        if (code == ElevationDeclined || code == ElevationLaunchFailed) return false;
        if (code != 0)
        {
            error = "did not complete (exit code " + code + ").";
            return false;
        }
        return true;
    }
}
