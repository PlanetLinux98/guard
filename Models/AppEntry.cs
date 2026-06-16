using System.ComponentModel;

namespace GuardWui3.Models;

// One installed-application row (App Management tab). Source is "winget" or
// "msstore" (a package id is known, so it can be reinstalled automatically) or
// "manual" (in Add/Remove Programs but not in any winget source).
public sealed partial class AppEntry : INotifyPropertyChanged
{
    private bool _include = true;
    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; OnChanged(nameof(Include)); } }
    }

    public string Name { get; set; } = "";
    public string Id { get; set; } = "";              // winget package id (auto apps only)
    public string Version { get; set; } = "";
    public string Source { get; set; } = "manual";    // "winget" | "msstore" | "manual"
    public string Publisher { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string PublisherUrl { get; set; } = "";

    public bool CanAuto => Source == "winget" || Source == "msstore";

    public string SourceLabel => Source switch
    {
        "winget" => "Winget",
        "msstore" => "Store",
        _ => "Manual"
    };

    // Spoken name for the row's checkbox (checked state announced by the role).
    public string Caption
    {
        get
        {
            string v = string.IsNullOrEmpty(Version) ? "" : ", version " + Version;
            return $"Application, {Name}{v}, {SourceLabel}" +
                (CanAuto ? ", reinstallable" : ", manual install");
        }
    }

    public override string ToString() => Caption;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
