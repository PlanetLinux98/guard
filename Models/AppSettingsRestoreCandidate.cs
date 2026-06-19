using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace GuardWui3.Models;

// One settings folder in the Restore App Settings confirmation dialog. Built
// from an export-side manifest entry: source is the copied folder under the
// imported list's AppSettings; target resolves by expanding the entry's
// rootAnchor (%APPDATA% etc.) against the CURRENT profile, so a restore lands
// right even if the username changed. Restore overwrites real user data, so each
// row is a CheckBox the user confirms; TargetExists drives the per-row "replaces
// existing" warning.
public sealed partial class AppSettingsRestoreCandidate : INotifyPropertyChanged
{
    private bool _include = true;
    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; OnChanged(nameof(Include)); } }
    }

    public string SourcePath { get; set; } = "";   // copied folder on disk (under AppSettings)
    public string FolderName { get; set; } = "";   // leaf name
    public string RootName { get; set; } = "";     // AppData | LocalAppData | DotConfig
    public string RootAnchor { get; set; } = "";   // %APPDATA% etc. - the manifest's anchor
    public string TargetPath { get; set; } = "";   // resolved destination on THIS machine
    public List<string> MatchedApps { get; } = new();

    public long Bytes { get; set; }                // recorded by the export, not re-measured
    public int Files { get; set; }
    // True when TargetPath exists, so restoring replaces a real folder; the
    // existing one is renamed aside first, never deleted.
    public bool TargetExists { get; set; }

    // Anchor form for display (e.g. %APPDATA%\Foo): compact and username-free,
    // unlike the expanded TargetPath the caption carries so the screen reader
    // still reads where the data lands.
    public string DisplayPath => RootAnchor + "\\" + FolderName;
    public string MatchedAppsLabel => string.Join(", ", MatchedApps);
    public string SizeLabel => FormatBytes(Bytes);
    public string StatusLabel => TargetExists ? "Replaces existing" : "New";

    // Spoken name for the row's checkbox (the role announces checked state). The
    // existing-folder reassurance is spelled out so the consequence is clear
    // without reading the dialog intro.
    public string Caption =>
        $"Settings folder, {DisplayPath}, {SizeLabel}, " +
        (TargetExists
            ? $"the target {TargetPath} already exists and will be replaced; your current copy is renamed aside first"
            : $"new, no existing folder at {TargetPath}") +
        $", restores {MatchedAppsLabel}";

    private static string FormatBytes(long b)
    {
        const double K = 1024.0;
        if (b >= 1024L * 1024 * 1024) return (b / (K * K * K)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        if (b >= 1024L * 1024) return (b / (K * K)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (b >= 1024L) return (b / K).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return b + " bytes";
    }

    public override string ToString() => Caption;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
