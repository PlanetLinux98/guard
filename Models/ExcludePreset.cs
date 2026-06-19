namespace GuardWui3.Models;

// Curated one-tick exclusion sets shown as checkboxes above the custom list.
// Ids are persisted in the ini (General.ExcludePresets); keep them stable
// across versions.
public sealed class ExcludePreset
{
    public string Id { get; }
    public string[] Dirs { get; }    // robocopy /XD tokens
    public string[] Files { get; }   // robocopy /XF tokens

    private ExcludePreset(string id, string[] dirs, string[] files)
    {
        Id = id;
        Dirs = dirs;
        Files = files;
    }

    public static readonly ExcludePreset Temp = new("temp",
        dirs: [],
        files: ["*.tmp", "*.bak", "~$*"]);

    public static readonly ExcludePreset System = new("system",
        dirs: ["$RECYCLE.BIN", "System Volume Information"],
        files: ["Thumbs.db", "desktop.ini", ".DS_Store"]);

    public static readonly ExcludePreset Dev = new("dev",
        dirs: ["node_modules", ".git", "bin", "obj", ".vs"],
        files: []);

    public static readonly ExcludePreset Cache = new("cache",
        dirs: ["cache", ".cache"],
        files: ["*.iso", "*.img"]);

    public static readonly ExcludePreset[] All = [Temp, System, Dev, Cache];
}
