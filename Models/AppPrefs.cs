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
    public string Theme = "System";      // System | Light | Dark
    public string StartupPage = "file";  // Tag of the nav page shown at launch
    // Windows toasts for unattended (scheduled / on-connect) backup runs.
    // Failures on, successes off by default: a failure needs acting on, a
    // nightly success toast is noise most people will not want.
    public bool NotifyFailure = true;
    public bool NotifySuccess = false;
}
