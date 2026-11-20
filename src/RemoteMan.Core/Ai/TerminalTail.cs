using System.Text;
using System.Text.RegularExpressions;

namespace RemoteMan.Core.Ai;

/// <summary>
/// Keeps the last part of a terminal's output as plain text (escape codes removed), so the
/// assistant can be shown what is on screen without asking the terminal for it.
/// </summary>
public sealed partial class TerminalTail(int maxChars = 32_000)
{
    private readonly StringBuilder _text = new();
    private string _carry = "";

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|\x1b[@-Z\\-_]|\x1b[()][0-9A-Za-z]")]
    private static partial Regex Escapes();

    public void Append(string chunk)
    {
        lock (_text)
        {
            var s = _carry + chunk;
            _carry = "";
            // Keep an unfinished escape sequence for the next chunk.
            var esc = s.LastIndexOf('\x1b');
            if (esc >= 0 && s.Length - esc < 64 && !Escapes().IsMatch(s[esc..]))
            {
                _carry = s[esc..];
                s = s[..esc];
            }
            s = Escapes().Replace(s, "");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\r': break;
                    case '\b': if (_text.Length > 0 && _text[^1] != '\n') _text.Length--; break;
                    case '\n': _text.Append('\n'); break;
                    case '\t': _text.Append(c); break;
                    default: if (!char.IsControl(c)) _text.Append(c); break;
                }
            }
            if (_text.Length > maxChars) _text.Remove(0, _text.Length - maxChars);
        }
    }

    /// <summary>The last <paramref name="lines"/> lines, trailing blank lines removed.</summary>
    public string LastLines(int lines)
    {
        string all;
        lock (_text) all = _text.ToString();
        var parts = all.Split('\n').Select(l => l.TrimEnd()).ToList();
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return string.Join('\n', parts.Skip(Math.Max(0, parts.Count - lines)));
    }
}

/// <summary>A piece of an assistant reply: prose, or a fenced code block.</summary>
public sealed record ReplySegment(bool IsCode, string Text, string? Language)
{
    /// <summary>Splits Markdown on ``` fences. An unclosed fence (still streaming) counts as code.</summary>
    public static IReadOnlyList<ReplySegment> Split(string text)
    {
        var result = new List<ReplySegment>();
        var pos = 0;
        while (pos < text.Length)
        {
            var open = text.IndexOf("```", pos, StringComparison.Ordinal);
            if (open < 0) { AddProse(text[pos..]); break; }
            AddProse(text[pos..open]);
            var lineEnd = text.IndexOf('\n', open);
            if (lineEnd < 0) break; // fence header still arriving
            var lang = text[(open + 3)..lineEnd].Trim();
            var close = text.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
            var code = close < 0 ? text[(lineEnd + 1)..] : text[(lineEnd + 1)..close];
            result.Add(new ReplySegment(true, code.TrimEnd('\n', '\r'), lang.Length > 0 ? lang : null));
            pos = close < 0 ? text.Length : close + 3;
        }
        return result;

        void AddProse(string s)
        {
            s = s.Trim('\n', '\r');
            if (s.Length > 0) result.Add(new ReplySegment(false, s, null));
        }
    }
}
