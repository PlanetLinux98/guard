namespace GuardWui3.Services;

// The path rules robocopy imposes on its two positional arguments, in one
// place. Both the generated backup script (quoted batch arguments) and the
// restore runner (ArgumentList, no shell) have to obey them, and a
// path-escaping helper that drifts between callers is how quoting bugs start
// (see ProcessRunner.PsQuote for the same reasoning).
public static class RobocopyPath
{
    // A path argument must never end in a backslash: robocopy parses \" as an
    // escaped quote and mangles every argument after it, and .NET's
    // ArgumentList doubles a trailing backslash for a quoted argument, which
    // robocopy's own parser does not undo. A bare or slashed drive root
    // becomes "X:\." ("X:" alone is drive-RELATIVE and would resolve against
    // that drive's current directory).
    public static string Arg(string? path)
    {
        string s = (path ?? "").Trim();
        if (s.Length == 2 && s[1] == ':') return s + "\\.";
        if (!s.EndsWith("\\")) return s;
        string t = s.TrimEnd('\\');
        return t.Length == 2 && t[1] == ':' ? t + "\\." : t;
    }

    // A destination that other segments are composed onto, so it must not end
    // in a backslash - except a drive root, which keeps "X:\" for the same
    // drive-relative reason as above (the "E:\\Sub" the composition then
    // yields is harmless to robocopy and cmd).
    public static string Root(string? dest)
    {
        string d = (dest ?? "").Trim();
        if (d.Length == 2 && d[1] == ':') return d + "\\";
        string t = d.TrimEnd('\\');
        return t.Length == 2 && t[1] == ':' ? t + "\\" : t;
    }
}
