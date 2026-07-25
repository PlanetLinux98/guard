using System;
using System.ComponentModel;

namespace GuardWui3.Models;

// Properties (not fields) so XAML data binding can two-way bind the include
// checkbox in the folder list.
public sealed partial class FolderPair : INotifyPropertyChanged
{
    private bool _include;
    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; OnChanged(nameof(Include)); } }
    }

    // Source/SubFolder raise change notifications (not plain auto-properties) so
    // an in-place edit of an existing pair refreshes its bound row; Caption is
    // derived from both, so it is re-raised alongside each.
    private string _source;                  // may contain %USERPROFILE% etc.
    public string Source
    {
        get => _source;
        set { if (_source != value) { _source = value; OnChanged(nameof(Source)); OnChanged(nameof(DisplaySource)); OnChanged(nameof(Caption)); } }
    }

    // List display, first-letter nav, and the screen reader all use the resolved
    // path, so %USERPROFILE%\Documents reads as the real folder. Source stays raw
    // to keep the generated script portable; paths with no variable pass through.
    public string DisplaySource => Environment.ExpandEnvironmentVariables(Source);

    private string _subFolder;               // name under the destination root
    public string SubFolder
    {
        get => _subFolder;
        set { if (_subFolder != value) { _subFolder = value; OnChanged(nameof(SubFolder)); OnChanged(nameof(Caption)); } }
    }

    // Which Windows known folder this row TRACKS ("Documents"), or empty for a
    // literal path the user chose themselves.
    //
    // GUARD deliberately keeps both this and Source. The identity records what
    // the user meant ("my Documents"); Source records the location actually
    // being backed up right now. They agree until Windows moves the folder -
    // OneDrive's "Back up your folders", or the folder's Location tab - and
    // GUARD then asks before following, because an unattended run must never
    // silently change what it protects. Until the answer comes, Source is what
    // the generated script keeps using.
    //
    // Not a notifying property: nothing binds to it, and it does not belong in
    // the row's spoken Caption (the user cares where the folder IS, which
    // DisplaySource already says).
    public string KnownFolder { get; set; } = "";

    public bool IsKnownFolder => KnownFolder.Length > 0;

    // The user deliberately gave this row a path of its own, so GUARD must not
    // adopt an identity for it again. Persisted separately from KnownFolder
    // because "never had one" and "had one, cleared on purpose" need opposite
    // treatment at load: without this, editing a row back to a path that happens
    // to match an old default would be silently re-tracked on the next launch,
    // and the offer to follow would come back.
    public bool Pinned { get; set; }

    public FolderPair(bool include, string source, string subFolder, string knownFolder = "")
    {
        _include = include;
        _source = source;
        _subFolder = subFolder;
        KnownFolder = knownFolder ?? "";
    }

    // Spoken name for the row's checkbox (the role announces checked state).
    public string Caption => $"Source folder, {DisplaySource}, destination subfolder, {SubFolder}";

    public override string ToString() => Caption;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
