using System.Text;
using System.Text.RegularExpressions;

namespace RemoteMan.Core.Terminal;

/// <summary>
/// Keeps the last part of a terminal's output as plain text (escape codes removed), so an
/// AI client (over MCP) can be shown what is on screen without asking the terminal for it.
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
