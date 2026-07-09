using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GuardWui3.Services;

// Incremental reader for a log another process is appending to (the elevated
// image/recovery scripts, whose output cannot cross the elevation boundary).
// Byte-offset based, and it only consumes up to the LAST newline: a line the
// writer has flushed halfway would otherwise be handed to the parser as two
// fragments, losing a "copied (NN%)" update or, worse, splitting an ERROR:
// line so the failure reason never gets captured.
public sealed class LogTail
{
    private readonly string _path;
    private long _pos;

    // startAtEnd skips everything already in the file: the image run tails a
    // log the elevated script only truncates AFTER the UAC prompt, so reading
    // from 0 would replay the previous run. A shrink (the truncation) rewinds
    // to 0 automatically.
    public LogTail(string path, bool startAtEnd)
    {
        _path = path;
        if (startAtEnd)
        {
            try { _pos = File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { _pos = 0; }
        }
    }

    // Complete lines appended since the last call, trimmed of CR and BOM,
    // empties dropped. FileShare.ReadWrite so the writer is never blocked.
    public List<string> ReadNewLines()
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(_path)) return lines;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _pos) _pos = 0;   // shrunk: the script rewrote the log
            if (fs.Length == _pos) return lines;
            fs.Seek(_pos, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _pos];
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return lines;

            int end = -1;
            for (int i = n - 1; i >= 0; i--)
                if (buf[i] == (byte)'\n') { end = i; break; }
            if (end < 0)
            {
                // No complete line yet; leave it for the next poll unless the
                // "line" has grown absurd (a writer that never emits newlines
                // must not stall the tail forever).
                if (n < 64 * 1024) return lines;
                end = n - 1;
            }

            _pos += end + 1;
            string text = Encoding.UTF8.GetString(buf, 0, end + 1);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').TrimStart('\uFEFF');
                if (line.Length > 0) lines.Add(line);
            }
        }
        catch { }
        return lines;
    }
}
