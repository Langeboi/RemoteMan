using System.Text.RegularExpressions;

namespace RemoteMan.Core.Chat;

public enum ChatSegmentKind { Text, Code }

/// <summary>A stretch of a message: ordinary text (with links and `inline code`) or a code block.</summary>
public sealed record ChatSegment(ChatSegmentKind Kind, string Text, string? Language = null)
{
    public int LineCount => Text.Length == 0 ? 0 : Text.Count(c => c == '\n') + 1;
}

/// <summary>
/// Finds code in chat messages so it can be shown compactly with a Copy button:
/// <c>```</c> fenced blocks (with an optional language), and unfenced pastes of several
/// lines that look like code, config, shell output or logs. Short prose stays text.
/// </summary>
public static partial class ChatFormat
{
    /// <summary>Unfenced runs need at least this many non-blank lines to become a code block.</summary>
    public const int MinCodeLines = 3;

    public static IReadOnlyList<ChatSegment> Split(string body)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var segments = new List<ChatSegment>();
        var prose = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var fence = Fence().Match(lines[i]);
            // ```ls -la``` on one line is inline code, not the start of a block.
            if (fence.Success && CountFences(lines[i]) == 1)
            {
                // Everything up to the closing fence (or the end, if it was never closed) is code.
                var code = new List<string>();
                var j = i + 1;
                while (j < lines.Length && !ClosingFence().IsMatch(lines[j])) code.Add(lines[j++]);
                // Text after ```lang on the fence line belongs to the code.
                if (fence.Groups[2].Value.Trim().Length > 0) code.Insert(0, fence.Groups[2].Value);
                AddProse(segments, prose, guess: true);
                var lang = fence.Groups[1].Value.Trim();
                var text = string.Join("\n", TrimBlank(code));
                if (text.Length > 0) segments.Add(new ChatSegment(ChatSegmentKind.Code, text, lang.Length > 0 ? lang : null));
                i = j;   // skip the closing fence
                continue;
            }
            prose.Add(lines[i]);
        }
        AddProse(segments, prose, guess: true);
        return segments;
    }

    /// <summary>Adds collected text lines, turning runs that look like code into code blocks.</summary>
    private static void AddProse(List<ChatSegment> segments, List<string> lines, bool guess)
    {
        if (lines.Count == 0) return;
        var start = 0;
        var i = 0;
        while (i < lines.Count)
        {
            if (!guess || !LooksLikeCode(lines[i])) { i++; continue; }
            // Grow the run over code-like lines; blank lines inside are fine.
            var j = i;
            var last = i;
            var codeLines = 0;
            while (j < lines.Count && (LooksLikeCode(lines[j]) || IsBlank(lines[j]) || (codeLines > 0 && Continues(lines[j]))))
            {
                if (!IsBlank(lines[j])) { codeLines++; last = j; }
                j++;
            }
            if (codeLines >= MinCodeLines)
            {
                AddText(segments, lines.GetRange(start, i - start));
                segments.Add(new ChatSegment(ChatSegmentKind.Code, string.Join("\n", lines.GetRange(i, last - i + 1))));
                i = last + 1;
                start = i;
            }
            else i = j;
        }
        AddText(segments, lines.GetRange(start, lines.Count - start));
        lines.Clear();
    }

    private static void AddText(List<ChatSegment> segments, List<string> lines)
    {
        var text = string.Join("\n", TrimBlank(lines));
        if (text.Length > 0) segments.Add(new ChatSegment(ChatSegmentKind.Text, text));
    }

    private static List<string> TrimBlank(List<string> lines)
    {
        var a = 0; var b = lines.Count;
        while (a < b && IsBlank(lines[a])) a++;
        while (b > a && IsBlank(lines[b - 1])) b--;
        return lines.GetRange(a, b - a);
    }

    private static int CountFences(string s) { var n = 0; for (var i = s.IndexOf("```"); i >= 0; i = s.IndexOf("```", i + 3)) n++; return n; }

    private static bool IsBlank(string s) => s.Trim().Length == 0;
    /// <summary>Inside a block: indented lines (" name servers") and column output ("3f2a   traefik:v3.2") belong to it.</summary>
    private static bool Continues(string s) => char.IsWhiteSpace(s[0]) || s.Trim().Contains("  ") || s.Contains('\t');

    private static bool Indented(string s) => s.StartsWith("  ") || s.StartsWith('\t');

    /// <summary>One line of code, config, a shell prompt or a log line (not ordinary prose).</summary>
    public static bool LooksLikeCode(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        if (ChatLink.Parse(t).Any(p => p.Link is not null)) return false;   // a line with links is a message
        if (Indented(line) && t.Length > 1) return true;
        if (CodePatterns().IsMatch(t)) return true;
        // Lots of symbols and few plain words: braces, operators, paths, addresses.
        var symbols = t.Count(c => "{}[]();=<>|&$#/\\:*\"'`_".Contains(c));
        return t.Length >= 8 && symbols * 4 >= t.Length && !t.EndsWith('?');
    }

    /// <summary>Splits a text line into plain and `inline code` pieces (code flag true).</summary>
    public static IReadOnlyList<(string Text, bool Code)> Inline(string text)
    {
        var list = new List<(string, bool)>();
        var at = 0;
        foreach (Match m in InlineCode().Matches(text))
        {
            if (m.Index > at) list.Add((text[at..m.Index], false));
            list.Add((m.Groups[2].Value, true));
            at = m.Index + m.Length;
        }
        if (at < text.Length) list.Add((text[at..], false));
        return list;
    }

    /// <summary>One line for previews and pop-ups: links as their names, code shown as "code".</summary>
    public static string Preview(string body)
    {
        var parts = Split(body).Select(s => s.Kind == ChatSegmentKind.Code ? $"[code, {s.LineCount} line{(s.LineCount == 1 ? "" : "s")}]" : ChatLink.Plain(s.Text));
        return string.Join(" ", parts).Replace('\n', ' ').Replace("`", "");
    }

    [GeneratedRegex(@"^\s*```\s*([A-Za-z0-9_+#.-]*)\s*(.*)$")]
    private static partial Regex Fence();
    [GeneratedRegex(@"^\s*```\s*$")]
    private static partial Regex ClosingFence();
    [GeneratedRegex(@"(`+)([^`\r\n]+?)\1")]
    private static partial Regex InlineCode();

    // Shell prompts, timestamps and log levels, config statements, code keywords and line endings.
    [GeneratedRegex(@"^(\$ |# |PS [A-Z]:\\|[\w.-]+@[\w.-]+[:~ ].*[$#]|\d{4}-\d\d-\d\d[ T]\d\d:\d\d|\[?\d\d:\d\d:\d\d|\[(INFO|WARN|WARNING|ERROR|DEBUG|TRACE)\]|(INFO|WARN|ERROR|DEBUG)\b|(interface|ip|ipv6|router|set|show|hostname|vlan|access-list|switchport|description|sudo|apt|dnf|yum|docker|kubectl|systemctl|journalctl|git|ssh|scp|curl|wget|cd|ls|cat|grep|echo|export|Get-|Set-|New-|Invoke-)\b|(def|class|function|func|fn|public|private|import|from|using|namespace|return|var|let|const|if|for|while|SELECT|INSERT|UPDATE|DELETE|CREATE)\b.*[(){}:;=]|[\w.-]+\s*=\s*\S|[\w.-]+:\s*\S*$|-\s+[\w.-]+:\s|<\/?[a-zA-Z][^>]*>|.*[;{}]$|//|/\*)")]
    private static partial Regex CodePatterns();
}
