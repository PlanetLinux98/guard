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

    // The list shows (and first-letter navigation matches, and the screen reader
    // speaks) the resolved path, so a default like %USERPROFILE%\Documents reads
    // as the actual folder rather than the variable. Source itself stays raw so
    // the generated backup script remains portable. Paths with no variable (e.g.
    // a browse-picked folder) pass through unchanged.
    public string DisplaySource => Environment.ExpandEnvironmentVariables(Source);

    private string _subFolder;               // name under the destination root
    public string SubFolder
    {
        get => _subFolder;
        set { if (_subFolder != value) { _subFolder = value; OnChanged(nameof(SubFolder)); OnChanged(nameof(Caption)); } }
    }

    public FolderPair(bool include, string source, string subFolder)
    {
        _include = include;
        _source = source;
        _subFolder = subFolder;
    }

    // Spoken name for the row's checkbox. The checked/unchecked state is
    // announced by the checkbox role itself, so it is NOT included here.
    public string Caption => $"Source folder, {DisplaySource}, destination subfolder, {SubFolder}";

    public override string ToString() => Caption;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
