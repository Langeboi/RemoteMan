using System.Globalization;
using System.Text.RegularExpressions;
using RemoteMan.Core.Model;
using RemoteMan.Core.Transfer;

namespace RemoteMan.Core.Import;

/// <summary>One saved PuTTY session: its name and its registry values (strings and dwords as text).</summary>
public sealed record PuttySession(string Name, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// PuTTY saved sessions, read from the registry (by the app) or from a .reg export of
/// HKCU\Software\SimonTatham\PuTTY\Sessions. SSH sessions only; serial, telnet and raw are skipped.
/// The .ppk named in a session is read and stored as a key credential.
/// </summary>
public static partial class PuttyImporter
{
    public const string RegistryPath = @"Software\SimonTatham\PuTTY\Sessions";

    /// <param name="readKeyFile">Returns a key file's text, or null if it can't be read.</param>
    public static ImportResult Import(IEnumerable<PuttySession> sessions, Func<string, string?>? readKeyFile = null)
    {
        var result = new ImportResult();
        var creds = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var v = session.Values;
            var name = Uri.UnescapeDataString(session.Name);
            if (name.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) continue;
            var protocol = Get(v, "Protocol") ?? "ssh";
            if (!protocol.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"{name}: {protocol} session skipped (only SSH is imported)");
                continue;
            }
            var host = Get(v, "HostName")?.Trim() ?? "";
            var user = Get(v, "UserName")?.Trim() ?? "";
            if (host.Contains('@'))
            {
                var at = host.LastIndexOf('@');
                if (user.Length == 0) user = host[..at];
                host = host[(at + 1)..];
            }
            if (host.Length == 0)
            {
                result.Warnings.Add($"{name}: no host name, skipped");
                continue;
            }
            var port = int.TryParse(Get(v, "PortNumber"), out var p) && p > 0 ? p : 22;

            var e = new Entry
            {
                Kind = EntryKind.Ssh,
                Name = name,
                Endpoints = { new HostEndpoint(host, port) },
                Ssh = new SshSettings
                {
                    StartupCommand = Get(v, "RemoteCommand") is { Length: > 0 } rc ? rc : null,
                },
            };
            if (int.TryParse(Get(v, "PingIntervalSecs"), out var ka) && ka > 0) e.Ssh.KeepAliveSeconds = ka;

            var keyPath = Get(v, "PublicKeyFile")?.Trim() ?? "";
            string? keyText = null;
            if (keyPath.Length > 0)
            {
                keyText = readKeyFile?.Invoke(keyPath);
                if (keyText is null) result.Warnings.Add($"{name}: key file {keyPath} could not be read");
            }

            if (user.Length > 0 || keyText is not null)
            {
                var credKey = $"{user}|{keyPath}";
                if (!creds.TryGetValue(credKey, out var cred))
                {
                    var label = keyText is not null ? $"{(user.Length > 0 ? user : "key")} ({Path.GetFileName(keyPath.Replace('\\', '/'))})" : user;
                    cred = new Entry { Kind = EntryKind.Credential, Name = label, Credential = new CredentialData { UserName = user.Length > 0 ? user : null } };
                    creds[credKey] = cred;
                    result.Items.Add(new ImportItem(cred) { PrivateKey = keyText });
                }
                e.CredentialId = cred.Id; e.Login = LoginSource.Saved;
            }
            result.Items.Add(new ImportItem(e));
        }
        result.SortAsTree();
        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) ? s : v.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Parses a regedit export (.reg, usually UTF-16) of the PuTTY Sessions key.</summary>
    public static List<PuttySession> ParseRegFile(string text)
    {
        var sessions = new List<PuttySession>();
        Dictionary<string, string>? current = null;
        foreach (var raw in JoinContinuations(text.Split('\n')))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                current = null;
                var m = SessionKey().Match(line);
                if (m.Success)
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sessions.Add(new PuttySession(m.Groups[1].Value, current));
                }
                continue;
            }
            if (current is null || !line.StartsWith('"')) continue;
            var vm = RegValue().Match(line);
            if (!vm.Success) continue;
            var name = Unescape(vm.Groups[1].Value);
            var value = vm.Groups[2].Value;
            if (value.StartsWith('"')) current[name] = Unescape(value.Trim('"'));
            else if (value.StartsWith("dword:", StringComparison.OrdinalIgnoreCase) &&
                     uint.TryParse(value[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dw))
                current[name] = dw.ToString(CultureInfo.InvariantCulture);
        }
        return sessions;
    }

    private static IEnumerable<string> JoinContinuations(IEnumerable<string> lines)
    {
        var buf = "";
        foreach (var l in lines)
        {
            var t = l.TrimEnd('\r');
            if (t.EndsWith('\\') && !t.EndsWith("\\\\\"")) { buf += t[..^1]; continue; }
            yield return buf + t;
            buf = "";
        }
        if (buf.Length > 0) yield return buf;
    }

    private static string Unescape(string s) => s.Replace("\\\\", "\u0001").Replace("\\\"", "\"").Replace("\u0001", "\\");

    [GeneratedRegex(@"^\[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\([^\\\]]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionKey();

    [GeneratedRegex(@"^""((?:[^""\\]|\\.)*)""=(.*)$")]
    private static partial Regex RegValue();
}
