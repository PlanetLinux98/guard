using System;
using System.Runtime.InteropServices;

namespace GuardWui3.Services;

// Volume identity for the drive-letter-drift feature: a USB drive saved as E:
// can come back as F:, silently orphaning the backup destination. The serial
// from GetVolumeInformationW is the same 8-hex-digit value Win32_LogicalDisk
// reports, so the app records it at save time and both the app (at the next
// save) and the generated script (at run time, via CIM) can re-find the
// volume wherever it landed.
public static class VolumeInfo
{
    public sealed record Volume(string Serial, string Label);

    // Identity of the volume behind a drive root ("E:\"), or null when the
    // drive is absent or unreadable. Only fixed/removable drives qualify: a
    // dead mapped network drive can hang the query for seconds, and serials
    // are only useful for letters that physically move.
    public static Volume? TryGetForRoot(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root)) return null;
            if (!root.EndsWith("\\")) root += "\\";
            uint type = GetDriveTypeW(root);
            if (type is not (DRIVE_FIXED or DRIVE_REMOVABLE)) return null;
            var label = new char[261];
            if (!GetVolumeInformationW(root, label, (uint)label.Length,
                    out uint serial, out _, out _, null, 0)) return null;
            int len = Array.IndexOf(label, '\0');
            return new Volume(serial.ToString("X8"),
                new string(label, 0, len < 0 ? label.Length : len));
        }
        catch { return null; }
    }

    // The drive root ("F:") currently carrying the volume with this serial,
    // or null. Scans local letters only, same rationale as TryGetForRoot.
    public static string? FindDriveBySerial(string serialHex)
    {
        if (string.IsNullOrWhiteSpace(serialHex)) return null;
        try
        {
            foreach (string drive in Environment.GetLogicalDrives())
            {
                var v = TryGetForRoot(drive);
                if (v != null && v.Serial.Equals(serialHex.Trim(), StringComparison.OrdinalIgnoreCase))
                    return drive.TrimEnd('\\');
            }
        }
        catch { }
        return null;
    }

    private const uint DRIVE_REMOVABLE = 2;
    private const uint DRIVE_FIXED = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string root, char[]? volumeName, uint volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags,
        char[]? fileSystemName, uint fileSystemNameSize);
}
