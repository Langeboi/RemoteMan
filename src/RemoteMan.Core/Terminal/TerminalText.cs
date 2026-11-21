using System.Text;
using System.Text.RegularExpressions;

namespace RemoteMan.Core.Terminal;

/// <summary>Terminal output as plain text: colour and cursor codes removed.</summary>
public static partial class TerminalText
{
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[()*+][0-9A-Za-z]|\x1b[=>78DEHMNOc]")]
    private static partial Regex Escapes();

    public static string Strip(string text) => Escapes().Replace(text, "");

    /// <summary>Plain text for a log file: escapes removed, carriage returns and other control characters dropped
    /// (tabs and new lines kept), backspaces applied.</summary>
    public static string ForLog(string text)
    {
        var plain = Strip(text);
        var sb = new StringBuilder(plain.Length);
        foreach (var c in plain)
        {
            if (c == '\b') { if (sb.Length > 0 && sb[^1] != '\n') sb.Length--; }
            else if (c is '\n' or '\t' || !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}
