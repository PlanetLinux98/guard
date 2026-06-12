namespace GuardWui3.Models;

// One user-defined exclusion in the custom list. Entries are added and removed
// whole (no per-entry enable flag); every listed entry applies.
public sealed class ExcludeItem
{
    public bool IsFolder { get; set; }   // true = robocopy /XD token, false = /XF
    public string Pattern { get; set; }

    public ExcludeItem(bool isFolder, string pattern)
    {
        IsFolder = isFolder;
        Pattern = pattern;
    }

    // Visible row label, which is also the list item's accessible name.
    public string Caption => (IsFolder ? "Folder name: " : "File pattern: ") + Pattern;

    public override string ToString() => Caption;
}
