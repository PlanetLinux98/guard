using System;
using System.Collections.Generic;
using System.Globalization;

namespace GuardWui3.Services;

// Accumulates robocopy's per-folder summary tables from the captured stdout
// stream (/TEE), one block per folder pair. Parsing the stream rather than
// re-reading the appended log avoids a second I/O pass; the log would need the
// same parsing anyway (it mixes runs with header banners).
//
// Locale strategy: localized Windows translates the row labels ("Files :" ->
// "Fichiers :"), so rows are identified by POSITION, not English words: a dashed
// rule, then a header line of column words, then rows always Dirs, Files, Bytes.
// Only the table SHAPE is matched (label, colon, six value cells). A locale that
// reorders or drops rows/columns would mis-map or fail the shape check; parsing
// then degrades silently and the caller falls back to the plain completion message.
public sealed class RobocopySummaryParser
{
    private enum State { Idle, SeenRule, SeenHeader, SeenDirs, SeenFiles }
    private State _state = State.Idle;

    public int Blocks { get; private set; }
    public long FilesCopied { get; private set; }
    public long FilesSkipped { get; private set; }
    public long FilesFailed { get; private set; }
    public long FilesExtras { get; private set; }
    public double BytesCopied { get; private set; }

    public void Feed(string line)
    {
        switch (_state)
        {
            case State.Idle:
                if (IsRule(line)) _state = State.SeenRule;
                break;

            case State.SeenRule:
                if (line.Trim().Length == 0) break;          // blank line between rule and header
                if (IsRule(line)) break;                     // banner's second rule; stay armed
                _state = IsHeader(line) ? State.SeenHeader : State.Idle;
                break;

            case State.SeenHeader:                           // Dirs row; values not needed
                _state = TryParseRow(line, out _) ? State.SeenDirs : State.Idle;
                break;

            case State.SeenDirs:                             // Files row
                if (TryParseRow(line, out var f))
                {
                    FilesCopied += (long)f[1];
                    FilesSkipped += (long)f[2];
                    FilesFailed += (long)f[4];
                    FilesExtras += (long)f[5];
                    _state = State.SeenFiles;
                }
                else _state = State.Idle;
                break;

            case State.SeenFiles:                            // Bytes row completes the block
                if (TryParseRow(line, out var b))
                {
                    BytesCopied += b[1];
                    Blocks++;
                }
                _state = State.Idle;
                break;
        }
    }

    // The rule above the summary is a long run of dashes; requiring 20+ keeps
    // short dashed decorations elsewhere from arming the state machine.
    private static bool IsRule(string line)
    {
        string t = line.Trim();
        if (t.Length < 20) return false;
        foreach (char c in t) if (c != '-') return false;
        return true;
    }

    // Column header: several words, no colon, no digits - distinguishes it from
    // the ROBOCOPY banner text and "Started :" lines that also follow rules.
    private static bool IsHeader(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;
        foreach (var t in tokens)
            foreach (char c in t)
                if (c == ':' || char.IsDigit(c)) return false;
        return true;
    }

    // A summary row is "<label> : v v v v v v" where each value is a number,
    // optionally followed by a separate one-letter size suffix in the Bytes row
    // ("1.234 g"). Exactly six values are required or the row is rejected.
    private static bool TryParseRow(string line, out double[] values)
    {
        values = Array.Empty<double>();
        int colon = line.IndexOf(':');
        if (colon < 0 || line.IndexOf("::", StringComparison.Ordinal) >= 0) return false;
        var tokens = line.Substring(colon + 1).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var vals = new List<double>(6);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!TryParseNumber(tokens[i], out double v)) return false;
            if (i + 1 < tokens.Length && TrySuffix(tokens[i + 1], out double mult))
            {
                v *= mult;
                i++;
            }
            vals.Add(v);
        }
        if (vals.Count != 6) return false;
        values = vals.ToArray();
        return true;
    }

    // Robocopy can print the Bytes fraction with the system decimal separator;
    // normalise a comma to a point and parse invariant.
    private static bool TryParseNumber(string token, out double value)
        => double.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TrySuffix(string token, out double mult)
    {
        mult = 1;
        if (token.Length != 1) return false;
        switch (char.ToLowerInvariant(token[0]))
        {
            case 'k': mult = 1024d; return true;
            case 'm': mult = 1024d * 1024; return true;
            case 'g': mult = 1024d * 1024 * 1024; return true;
            case 't': mult = 1024d * 1024 * 1024 * 1024; return true;
            default: return false;
        }
    }
}
