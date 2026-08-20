namespace GuardWui3.Models;

// GUARD's own application preferences (guard-prefs.ini), kept apart from the
// backup configuration (backup-settings.ini): these persist immediately on
// change, while backup settings wait for an explicit Save Settings, and mixing
// the two would muddy that save-button contract.
public sealed class AppPrefs
{
    public bool UpdateAutoCheck = true;
    public bool UpdateAutoInstall = false;
    public string SkippedVersion = "";   // release tag the user chose to skip
    public string LastUpdateCheck = "";  // yyyy-MM-dd of the last successful check
    public string Theme = "System";         // System | Light | Dark
    public string StartupPage = "status";   // Tag of the nav page shown at launch
    // Schema marker for one-time migrations, written by Save and read by Load.
    // Absent from the file means it was written before 0.6, which is the only
    // way to tell an old written default apart from a deliberate choice.
    public const int CurrentPrefsVersion = 2;
    public int PrefsVersion;
    // Windows toasts for unattended (scheduled / on-connect) backup runs.
    // Failures on, successes off by default: a failure needs acting on, a
    // nightly success toast is noise most people will not want.
    public bool NotifyFailure = true;
    public bool NotifySuccess = false;
    // Separator for the two path lists below. "|", not ";": a semicolon is a
    // LEGAL Windows filename character, so a folder called "Q3;final" would
    // split into two entries that match nothing, silently losing the answer the
    // user gave. "|" cannot appear in a path at all, which is why the folder
    // rows in backup-settings.ini already use it. (">" inside a DeclinedMoves
    // key is safe for the same reason.)
    public const char ListSeparator = '|';
    // Moves of a tracked personal folder the user has declined to follow, as
    // ListSeparator-separated "<folder>><new path>" keys (see
    // KnownFolders.Moved.Key). The destination is part of the key on purpose:
    // keyed on the folder NAME alone, declining "Documents moved to OneDrive"
    // would also silence "Documents moved to D:\Docs" years later, which is a
    // different question.
    public string DeclinedMoves = "";
    // Sources the user has acknowledged as deliberately empty, as
    // ListSeparator-separated expanded paths. Needed because in Additive mode
    // the backup never loses the files it already holds, so a folder emptied on
    // purpose satisfies the vanished condition for ever: without a way to say
    // "yes, I know", the warning would repeat on every launch, every save and
    // every scheduled run with no way to stop it. Cleared for a path
    // automatically once that folder has content again, so a real later
    // disappearance still speaks up.
    public string AcknowledgedEmpty = "";
}
