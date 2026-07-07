using System.IO;

namespace GuardWui3.Services;

// Crash-safe replacement for File.WriteAllText on the files other components
// depend on (settings inis, generated scripts): write a sibling temp file,
// then swap it into place, so a crash or power cut mid-write can never leave
// a truncated file for the next launch or a scheduled task to read.
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        // File.Replace needs an existing destination; first-ever save moves.
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
