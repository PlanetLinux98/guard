using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace GuardWui3.Models;

// One candidate settings folder offered in the Export App Settings confirmation
// dialog. The folder-to-app matching is heuristic (folder names compared against
// app names and publishers), so each row is a real CheckBox the user confirms or
// unticks before anything is copied.
public sealed partial class AppSettingsCandidate : INotifyPropertyChanged
{
    private bool _include = true;
    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; OnChanged(nameof(Include)); } }
    }

    public string FolderPath { get; set; } = "";   // full source path on disk
    public string FolderName { get; set; } = "";   // leaf name under the root
    public string RootName { get; set; } = "";     // AppData | LocalAppData | DotConfig
    public string RootAnchor { get; set; } = "";   // e.g. %APPDATA% - recorded in the manifest
    public List<string> MatchedApps { get; } = new();

    public long Bytes { get; set; }
    public int Files { get; set; }
    // True when the size pre-scan hit its time/file cap, so Bytes is a floor,
    // not a total; surfaced as "at least" so the user is never misled.
    public bool SizePartial { get; set; }

    // Sizes are measured in the background after the dialog opens, so the row
    // shows "Calculating..." until its measurement lands. Measured flips (and
    // the bound labels refresh) via NotifyMeasured, which the dialog calls on
    // the UI thread.
    public bool Measured { get; private set; }
    public void NotifyMeasured()
    {
        Measured = true;
        OnChanged(nameof(SizeLabel));
        OnChanged(nameof(Caption));
    }

    public string DisplayPath => RootName + "\\" + FolderName;
    public string MatchedAppsLabel => string.Join(", ", MatchedApps);
    public string SizeLabel => Measured
        ? (SizePartial ? "at least " : "") + FormatBytes(Bytes)
        : "Calculating...";

    // Spoken name for the row's checkbox (checked state announced by the role).
    public string Caption =>
        $"Settings folder, {DisplayPath}, {SizeLabel}, matched to {MatchedAppsLabel}";

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
