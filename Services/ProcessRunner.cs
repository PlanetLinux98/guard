using System;
using System.Diagnostics;
using System.IO;
using System.Text;

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
        string o = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    // Runs `winget install` for one id, streaming combined output via onLine.
    // Returns the process exit code. attach receives the live Process so the
    // caller can hold a reference for cancellation.
    public static int RunWingetInstall(string id, Action<string> onLine, Action<Process>? attach = null)
    {
        var psi = new ProcessStartInfo("winget",
            "install --id \"" + id + "\" -e --silent --accept-package-agreements --accept-source-agreements")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data + "\r\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data + "\r\n"); };
        p.Start();
        attach?.Invoke(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    // Run PowerShell, optionally returning a problem message (null on success).
    public static string? RunPowerShell(string script)
    {
        try
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
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode != 0 ? err : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
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
        string outp = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return outp;
    }

    // Run a PowerShell script ELEVATED (UAC prompt). Output cannot cross the
    // elevation boundary, so the script goes to a temp .ps1 and only the exit
    // code is checked. Returns true on success, false on failure/cancellation.
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
