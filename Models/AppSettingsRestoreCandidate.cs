using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace GuardWui3.Models;

// One settings folder offered in the Restore App Settings confirmation dialog.
// Built from a manifest entry written by the export side: the source is the
// copied folder under the imported list's AppSettings folder, and the target is
// resolved by expanding the entry's rootAnchor (%APPDATA% etc.) against the
// CURRENT user profile, so a restore lands correctly even if the Windows
// username changed. The restore overwrites real user data, so each row is a
// real CheckBox the user confirms - and TargetExists drives the per-row
// "replaces existing" warning before anything is touched.
public sealed class AppSettingsRestoreCandidate : INotifyPropertyChanged
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
    // True when TargetPath already exists, so restoring it replaces a real
    // folder; the existing one is renamed aside first, never deleted outright.
    public bool TargetExists { get; set; }

    // Anchor form for display (e.g. %APPDATA%\Foo): compact and free of the
    // user name, unlike the fully expanded TargetPath (which the caption carries
    // so a screen reader still reads where the data actually lands).
    public string DisplayPath => RootAnchor + "\\" + FolderName;
    public string MatchedAppsLabel => string.Join(", ", MatchedApps);
    public string SizeLabel => FormatBytes(Bytes);
    public string StatusLabel => TargetExists ? "Replaces existing" : "New";

    // Spoken name for the row's checkbox (the role announces checked state). The
    // existing-folder reassurance is spelled out here so the consequence is clear
    // without the user having to read the dialog's intro text.
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
