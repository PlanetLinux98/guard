using System.Globalization;

namespace GuardWui3.Models;

// Compact byte label for list-row captions and the run summary (previously
// duplicated in the two settings-candidate models and MainWindow).
// SaveValidation.FormatBytes stays separate: the status-bar lines use its
// one-decimal "0.0" style.
internal static class SizeText
{
    public static string FormatBytes(long b)
    {
        const double K = 1024.0;
        if (b >= 1024L * 1024 * 1024 * 1024) return (b / (K * K * K * K)).ToString("0.#", CultureInfo.InvariantCulture) + " TB";
        if (b >= 1024L * 1024 * 1024) return (b / (K * K * K)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        if (b >= 1024L * 1024) return (b / (K * K)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (b >= 1024L) return (b / K).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return b + " bytes";
    }
}
