using RemoteMan.Core.Model;

namespace RemoteMan.Core.Terminal;

/// <summary>
/// Runs an entry's login script against the terminal output: for each step, wait until the output
/// (after the previous step was typed) contains the step's text, then type its reply and Enter.
/// A step with nothing to wait for is typed straight away.
/// </summary>
public sealed class LoginScriptRunner
{
    private readonly IReadOnlyList<ScriptStep> _steps;
    private readonly Func<string, string> _expand;
    private readonly Action<string> _send;
    private readonly string _enter;
    private string _seen = "";
    private int _next;

    public LoginScriptRunner(IReadOnlyList<ScriptStep> steps, Func<string, string> expand, Action<string> send, string enter = "\r")
    {
        _steps = steps.Where(s => !string.IsNullOrEmpty(s.WaitFor) || !string.IsNullOrEmpty(s.Send)).ToList();
        _expand = expand;
        _send = send;
        _enter = enter;
    }

    public bool Done => _next >= _steps.Count;

    /// <summary>Types any leading steps that don't wait for anything.</summary>
    public void Start() => Advance();

    public void Feed(string output)
    {
        if (Done) return;
        _seen += TerminalText.Strip(output);
        if (_seen.Length > 8192) _seen = _seen[^4096..];
        Advance();
    }

    private void Advance()
    {
        while (!Done)
        {
            var step = _steps[_next];
            if (!string.IsNullOrEmpty(step.WaitFor))
            {
                var i = _seen.IndexOf(step.WaitFor, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return;
                _seen = _seen[(i + step.WaitFor.Length)..];
            }
            _next++;
            if (step.Send is { } text) _send(_expand(text) + _enter);
            // Only what comes after this reply counts for the next step (not the prompt we just answered).
            _seen = "";
        }
    }

    /// <summary>Fills {field:Name}, {secret:Name}, {totp}, {host}, {user} and {name}.</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> fields, Func<string?> totp, string host, string? user, string name)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, @"\{(field|secret):([^}]+)\}",
            m => fields.TryGetValue(m.Groups[2].Value.Trim(), out var v) ? v : "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (result.Contains("{totp}", StringComparison.OrdinalIgnoreCase))
            result = result.Replace("{totp}", totp() ?? "", StringComparison.OrdinalIgnoreCase);
        return result.Replace("{host}", host).Replace("{user}", user ?? "").Replace("{name}", name);
    }
}
