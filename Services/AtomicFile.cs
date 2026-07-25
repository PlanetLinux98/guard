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
        // The folder may not exist yet: under a winget install GUARD's working
        // files live in %LOCALAPPDATA%\GUARD, which nothing else creates before
        // the first save (see GuardPaths.DataDir).
        string? dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        // File.Replace needs an existing destination; first-ever save moves.
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
