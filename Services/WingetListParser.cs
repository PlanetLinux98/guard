using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GuardWui3.Services;

// One parsed `winget list` row. Kept dumb (no AppEntry knowledge) so the
// column parsing is a pure, testable function apart from the registry merge.
public sealed class WingetRow
{
    public string Name = "";
    // The Name cell ended in winget's truncation ellipsis: the full app name
    // did not fit the column, so Name is a PREFIX of the real name and must
    // not be trusted for exact matching.
    public bool NameTruncated;
    public string Id = "";
    public string Version = "";
    public string Source = "";   // "winget" | "msstore" | "" (registry-only row)
    // Reinstallable by id: a real package id from a source, not a truncated
    // one and not winget's ARP\... synthetic ids for registry-only entries.
    public bool CanAuto => Id.Length > 0 && !Id.StartsWith("ARP\\", StringComparison.Ordinal)
        && Id.IndexOf('\\') < 0 && !Id.EndsWith("…", StringComparison.Ordinal);
}

// Parses `winget list` console output. Column positions come from the header
// line; localized winget translates the column WORDS but keeps their order
// (Name, Id, Version, [Available], Source), so the fallbacks work by position.
// Source names themselves ("winget", "msstore") are literal in every locale.
public static class WingetListParser
{
    private const char Ellipsis = '…';

    public static List<WingetRow> Parse(string raw)
    {
        var rows = new List<WingetRow>();
        // winget prints a CR-only spinner preamble; split on both CR and LF
        // (dropping empties) so the header line stands alone.
        string[] lines = (raw ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        int hi = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.IndexOf("Name", StringComparison.Ordinal) >= 0 &&
                l.IndexOf("Id", StringComparison.Ordinal) >= 0 &&
                l.IndexOf("Version", StringComparison.Ordinal) >= 0)
            { hi = i; break; }
        }
        if (hi < 0)
        {
            // Localized header: find the locale-neutral all-dash separator and
            // take the line above it.
            for (int i = 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length >= 10 && IsAllDashes(t)) { hi = i - 1; break; }
            }
        }
        if (hi < 0) return rows;

        // The Id column starts at the header's "Id" word or, on a localized
        // header, at its second whitespace-delimited word.
        int idStart = lines[hi].IndexOf("Id", StringComparison.Ordinal);
        if (idStart < 0)
        {
            var m = Regex.Match(lines[hi], @"^\S+\s+(\S)");
            idStart = m.Success ? m.Groups[1].Index : -1;
        }
        if (idStart <= 0) return rows;

        // Version's and Available's own column starts, found the same way as
        // Id above (literal word, or the third/fourth whitespace-delimited
        // word on a localized header). Needed below to tell a genuinely blank
        // Version cell apart from a populated one when a row's tail
        // tokenizes to the same shape either way - see the 3-token case.
        int versionStart = lines[hi].IndexOf("Version", StringComparison.Ordinal);
        if (versionStart < 0)
        {
            var m = Regex.Match(lines[hi], @"^\S+\s+\S+\s+(\S)");
            versionStart = m.Success ? m.Groups[1].Index : -1;
        }
        // Available appears only when at least one installed package has an
        // upgrade; when the header has no such column this stays -1, and the
        // 3-token ambiguity below never arises (there is no fourth cell to
        // confuse Version with).
        int availableStart = lines[hi].IndexOf("Available", StringComparison.Ordinal);
        if (availableStart < 0)
        {
            var m = Regex.Match(lines[hi], @"^\S+\s+\S+\s+\S+\s+(\S)");
            availableStart = m.Success ? m.Groups[1].Index : -1;
        }

        int start = hi + 1;
        if (start < lines.Length && lines[start].TrimStart().StartsWith("---")) start++;

        for (int i = start; i < lines.Length; i++)
        {
            string row = lines[i];
            if (row.Trim().Length == 0) continue;

            string name = (idStart <= row.Length ? row.Substring(0, idStart) : row).Trim();
            bool truncated = name.EndsWith(Ellipsis);
            name = name.TrimEnd(Ellipsis).Trim();
            if (name.Length == 0) continue;

            string tail = row.Length > idStart ? row.Substring(idStart) : "";
            var toks = Regex.Matches(tail, "\\S+");

            // Blank cells vanish under whitespace tokenizing, so positions
            // shift: [id], [id version], [id version source], [id version
            // available source], [id source]... The source is recovered from
            // the LAST token (source names are literal in every locale) and
            // the version is the second token only when it is not that source
            // token - this is what mis-filed the Available/Source value as a
            // version whenever the Version cell was empty.
            string id = toks.Count > 0 ? toks[0].Value : "";
            string source = "";
            if (toks.Count >= 2)
            {
                string last = toks[toks.Count - 1].Value;
                if (last is "winget" or "msstore") source = last;
            }
            string ver = "";
            if (toks.Count >= 2 && !(toks.Count == 2 && source.Length > 0))
            {
                // Three tokens [id, X, source] is ambiguous whenever the header
                // has an Available column: X is Version with Available blank,
                // OR Available with Version itself blank - a blank cell leaves
                // no token at all, so both tokenize identically and X's own
                // text can't say which column it came from (both look like
                // version strings). Fall back to whether the row actually has
                // anything in the Version column's own character range.
                bool ambiguous = toks.Count == 3 && versionStart > 0 && availableStart > versionStart;
                bool versionCellBlank = ambiguous && IsBlank(row, versionStart, availableStart);
                if (!versionCellBlank) ver = toks[1].Value;
            }
            if (ver == "…") ver = "";

            rows.Add(new WingetRow
            {
                Name = name,
                NameTruncated = truncated,
                Id = id,
                Version = ver,
                Source = source,
            });
        }
        return rows;
    }

    // Whether row[start..end) (clamped to the row's actual length, since a
    // trailing blank cell can leave the row shorter than the header) has no
    // non-whitespace character - i.e. that column's cell is blank for this row.
    private static bool IsBlank(string row, int start, int end)
    {
        int from = Math.Min(start, row.Length);
        int to = Math.Min(end, row.Length);
        for (int i = from; i < to; i++)
            if (!char.IsWhiteSpace(row[i])) return false;
        return true;
    }

    private static bool IsAllDashes(string s)
    {
        foreach (char c in s) if (c != '-') return false;
        return true;
    }
}
