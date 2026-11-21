using System.Text;

namespace RemoteMan.Protocols.Terminal;

/// <summary>Character set of a terminal entry (UTF-8 unless the entry picks an older one, e.g. ISO-8859-1 or IBM437).</summary>
public static class TerminalEncoding
{
    static TerminalEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static readonly (string Name, string Label)[] Choices =
    [
        ("", "UTF-8"),
        ("iso-8859-1", "ISO-8859-1 (Latin-1, old switches)"),
        ("iso-8859-15", "ISO-8859-15 (Latin-9)"),
        ("windows-1252", "Windows-1252"),
        ("ibm437", "IBM437 (DOS, line drawing)"),
        ("ibm850", "IBM850 (DOS Western Europe)"),
    ];

    public static Encoding For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
        try { return Encoding.GetEncoding(name.Trim()); }
        catch (ArgumentException) { return new UTF8Encoding(false); }
    }
}
