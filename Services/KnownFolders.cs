using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using GuardWui3.Models;

namespace GuardWui3.Services;

// Where the user's personal folders ACTUALLY live, and how GUARD keeps up when
// Windows moves them.
//
// GUARD's defaults used to be the literal strings %USERPROFILE%\Documents,
// ...\Desktop and so on, which only holds until something moves them - and two
// common things do. OneDrive's "Back up your folders" (on by default on many
// Microsoft-account PCs) relocates Documents, Desktop and Pictures under
// %USERPROFILE%\OneDrive; a folder's own Properties -> Location tab can send it
// anywhere, typically a second drive. Either way Windows updates the known
// folder and the old path stops being the user's data.
//
// The dangerous half is that the vacated folder is often LEFT BEHIND rather
// than deleted, holding just a desktop.ini. Backing up the literal path then
// copies essentially nothing, robocopy reports no error, and the run is
// recorded as a success - the user's documents are simply not in the backup and
// nothing says so.
//
// So a default row stores the IDENTITY ("Documents") alongside the path it is
// currently backing up. When the two diverge GUARD offers to follow; it never
// follows on its own, because a backup tool must not silently change what it
// protects, and an unattended run has nobody to ask.
public static class KnownFolders
{
    // Name is the identity stored in FolderPair.KnownFolder and doubles as the
    // destination subfolder for a default row. LegacyPath is what releases up
    // to v0.5.2 hard-coded, kept only so an existing configuration can be
    // recognized and adopted (see AdoptIdentities).
    private sealed record Definition(string Name, string LegacyPath, Func<string?> Resolve);

    // Order matches the pre-0.6 default list so an existing user's rows are not
    // reshuffled.
    private static readonly Definition[] Defaults =
    {
        new("Documents", @"%USERPROFILE%\Documents",  () => Special(Environment.SpecialFolder.MyDocuments)),
        new("Videos",    @"%USERPROFILE%\Videos",     () => Special(Environment.SpecialFolder.MyVideos)),
        // DesktopDirectory, not Desktop: the latter is the virtual shell folder,
        // which has no usable filesystem path.
        new("Desktop",   @"%USERPROFILE%\Desktop",    () => Special(Environment.SpecialFolder.DesktopDirectory)),
        new("Pictures",  @"%USERPROFILE%\Pictures",   () => Special(Environment.SpecialFolder.MyPictures)),
        new("Music",     @"%USERPROFILE%\Music",      () => Special(Environment.SpecialFolder.MyMusic)),
        new("Favorites", @"%USERPROFILE%\Favorites",  () => Special(Environment.SpecialFolder.Favorites)),
        // Contacts has no SpecialFolder member, so it goes through the shell's
        // known-folder API directly.
        new("Contacts",  @"%USERPROFILE%\Contacts",   () => KnownFolderPath(FolderIdContacts)),
    };

    // The identities GUARD understands. A value from a hand-edited settings file
    // that is not one of these is dropped, so a row can never end up tracking
    // something that resolves nowhere.
    public static bool IsKnownIdentity(string? name)
    {
        foreach (var d in Defaults)
            if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Where Windows says this folder is NOW, %USERPROFILE%-encoded where it can
    // be. Null when the identity is unknown or the shell cannot resolve it - the
    // caller then leaves the row's existing Source alone rather than guessing.
    public static string? Resolve(string? name)
    {
        foreach (var d in Defaults)
        {
            if (!string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string? p = d.Resolve();
                return string.IsNullOrWhiteSpace(p) ? null : Encode(p!);
            }
            catch { return null; }
        }
        return null;
    }

    // The default folder list for a fresh configuration: every row carries its
    // identity AND starts at the resolved location, so a new install is correct
    // on day one and has nothing to ask about.
    public static ObservableCollection<FolderPair> DefaultFolderPairs()
    {
        var pairs = new ObservableCollection<FolderPair>();
        foreach (var d in Defaults)
            pairs.Add(new FolderPair(true, Resolve(d.Name) ?? d.LegacyPath, d.Name, d.Name));
        return pairs;
    }

    // Give identities to rows saved before GUARD tracked them: a row still
    // holding one of the old hard-coded default paths is, by construction, the
    // default row for that folder.
    //
    // Deliberately silent, and deliberately does NOT touch Source. Adopting the
    // identity changes nothing about what gets backed up; it only means GUARD
    // can now notice if that folder ever moves, and ask. A user who typed the
    // same path by hand loses nothing either - the worst case is one question
    // they can decline.
    public static void AdoptIdentities(IEnumerable<FolderPair> folders)
    {
        foreach (var f in folders)
        {
            // Pinned means the user edited this row to a path of their own.
            // Adopting an identity for it again would silently resume tracking a
            // row they took manual control of, and bring the follow prompt back.
            if (f.IsKnownFolder || f.Pinned) continue;
            string src = Normalize(f.Source);
            if (src.Length == 0) continue;
            foreach (var d in Defaults)
                if (src.Equals(Normalize(d.LegacyPath), StringComparison.OrdinalIgnoreCase))
                {
                    f.KnownFolder = d.Name;
                    break;
                }
        }
    }

    // A tracked row whose folder Windows now reports somewhere else.
    public sealed record Moved(FolderPair Pair, string CurrentSource, string ResolvedSource)
    {
        // Identifies THIS move (which folder, and where to), so declining it
        // silences only this move. A later move of the same folder to a
        // different place is a different key and gets asked about properly.
        public string Key => Pair.KnownFolder + ">" + ResolvedSource;
    }

    public static List<Moved> FindMoved(IEnumerable<FolderPair> folders)
    {
        var moved = new List<Moved>();
        foreach (var f in folders)
        {
            if (!f.Include || !f.IsKnownFolder) continue;
            string? resolved = Resolve(f.KnownFolder);
            if (resolved is null) continue;
            if (Normalize(f.Source).Equals(Normalize(resolved), StringComparison.OrdinalIgnoreCase)) continue;
            moved.Add(new Moved(f, f.Source, resolved));
        }
        return moved;
    }

    // Expanded, separator-trimmed form for comparing two path spellings.
    private static string Normalize(string? path)
    {
        string p = Environment.ExpandEnvironmentVariables((path ?? "").Trim());
        return p.TrimEnd('\\', '/');
    }

    private static string? Special(Environment.SpecialFolder id)
    {
        // SpecialFolderOption.None asks for the CURRENT path and does not create
        // the folder; an unresolvable folder comes back as "".
        string p = Environment.GetFolderPath(id);
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    // Re-encode an absolute path under the profile as %USERPROFILE%\..., which
    // is what GUARD has always stored: the generated script expands it at RUN
    // time, so the same configuration keeps working for a different user, and
    // the variable dodges the console-codepage problem that bites literal
    // non-ASCII paths (see BackupScript.Generate). A path outside the profile
    // (Documents moved to D:\Data, say) has no such spelling and stays absolute.
    private static string Encode(string full)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (profile.Length > 0
            && full.Length > profile.Length
            && full.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            && full[profile.Length] == '\\')
            return "%USERPROFILE%" + full.Substring(profile.Length);
        return full;
    }

    private static readonly Guid FolderIdContacts = new("56784854-C6CB-462B-8169-88E350ACB882");

    private static string? KnownFolderPath(Guid id)
    {
        nint p = 0;
        try
        {
            // KF_FLAG_DEFAULT (0): the folder's current path, no creation.
            if (SHGetKnownFolderPath(ref id, 0, 0, out p) != 0) return null;
            return Marshal.PtrToStringUni(p);
        }
        catch { return null; }
        finally { if (p != 0) Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
