using System;
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

    // Runs `winget install` for one id, streaming combined output via onLine;
    // returns the exit code. Cancelling the token kills the whole winget process
    // tree, making WaitForExit return. Killing inside this method (not handing
    // the Process to the caller) keeps the kill within the using scope, so a
    // cancel can't race disposal. Kill throws if the process already exited -
    // benign, so swallowed.
    public static int RunWingetInstall(string id, Action<string> onLine, CancellationToken ct = default)
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
        // ArgumentList, not a concatenated string: the id comes from an imported
        // app-list.json (hand-editable), so a quote in it must be escaped as data
        // rather than splice extra arguments into the command line.
        foreach (var a in new[] { "install", "--id", id, "-e", "--silent",
                 "--accept-package-agreements", "--accept-source-agreements" })
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
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
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

    // Run a PowerShell script ELEVATED (UAC prompt). Output can't cross the
    // elevation boundary, so the script goes to a temp .ps1 and only the exit
    // code is checked. True on success, false on failure/cancellation.
    public static bool RunPowerShellElevated(string script, out string? error)
    {
        error = null;
        string ps1 = Path.Combine(Path.GetTempPath(), "guard_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(ps1, script);
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                error = "did not complete (exit code " + p.ExitCode + ").";
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            error = "was cancelled - Administrator approval was declined.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(ps1); } catch { }
        }
    }
}
