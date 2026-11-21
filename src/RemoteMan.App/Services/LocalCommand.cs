using System.Diagnostics;

namespace RemoteMan.App.Services;

/// <summary>An entry's "run before connecting" / "run after closing" command, through cmd.exe on this PC.</summary>
public static class LocalCommand
{
    /// <summary>Starts the command; with <paramref name="wait"/>, waits up to two minutes for it to finish.
    /// Returns an error text, or null when it started (and, if waited for, ended with exit code 0).</summary>
    public static async Task<string?> RunAsync(string command, bool wait, IReadOnlyDictionary<string, string>? vars = null)
    {
        var text = command;
        if (vars is not null) foreach (var (k, v) in vars) text = text.Replace("{" + k + "}", v, StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(text);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "it didn't start";
            if (!wait) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return "still running after two minutes"; }
            return p.ExitCode == 0 ? null : $"exit code {p.ExitCode}";
        }
        catch (Exception ex) { return ex.Message; }
    }
}
