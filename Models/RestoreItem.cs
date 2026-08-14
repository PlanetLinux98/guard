using System.ComponentModel;
using GuardWui3.Services;

namespace GuardWui3.Models;

// One row in the File Restore dialog: a folder found inside the backup, and
// the live folder its copies would go back to.
//
// Properties (not fields) and partial, like FolderPair, so the row can be
// two-way bound and so the WinRT source generator can extend it under
// NativeAOT. Target notifies because the user can redirect a row - on a new PC
// the original path may not exist - and the spoken row caption has to follow.
public sealed partial class RestoreItem : INotifyPropertyChanged
{
    private bool _include;
    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; OnChanged(nameof(Include)); } }
    }

    // The folder's name under the backup ("Documents", "Work\Reports").
    public string FolderName { get; }

    // Where its copies are: <snapshot>\<FolderName>.
    public string SourcePath { get; }

    private string _target;
    public string Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            OnChanged(nameof(Target));
            OnChanged(nameof(TargetDisplay));
            OnChanged(nameof(Caption));
        }
    }

    // Never blank in the list: an empty cell reads as a column that failed to
    // load, where "choose a folder" reads as the instruction it is.
    public string TargetDisplay => _target.Length > 0 ? _target : "(choose a folder)";

    // Where the suggested target came from, so the dialog can say whether GUARD
    // is repeating the user's own setting or guessing from Windows.
    public TargetOrigin Origin { get; }

    public RestoreItem(RestoreCandidate c)
    {
        FolderName = c.FolderName;
        SourcePath = c.SourcePath;
        _target = c.SuggestedTarget;
        Origin = c.Origin;
        // Ticked by default only when there is somewhere to put it. A row whose
        // target is unknown would otherwise fail the dialog's own validation the
        // moment Restore is pressed, on a machine where the user may not know
        // what the folder was.
        _include = _target.Length > 0;
    }

    // Spoken name for the row's checkbox (the role announces checked state).
    public string Caption => "Backup folder " + FolderName + ", restore into " + TargetDisplay;

    public override string ToString() => Caption;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
