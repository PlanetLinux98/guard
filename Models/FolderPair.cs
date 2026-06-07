using System.ComponentModel;

namespace GuardWui3.Models;

// Properties (not fields) so XAML data binding can two-way bind the include
// checkbox in the folder list.
public sealed class FolderPair : INotifyPropertyChanged
{
    private bool _include;
    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; OnChanged(nameof(Include)); } }
    }

    public string Source { get; set; }      // may contain %USERPROFILE% etc.
    public string SubFolder { get; set; }    // name under the destination root

    public FolderPair(bool include, string source, string subFolder)
    {
        _include = include;
        Source = source;
        SubFolder = subFolder;
    }

    // Spoken name for the row's checkbox. The checked/unchecked state is
    // announced by the checkbox role itself, so it is NOT included here.
    public string Caption => $"Source folder, {Source}, destination subfolder, {SubFolder}";

    public override string ToString() => Caption;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
