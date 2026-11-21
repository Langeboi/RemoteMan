using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// The Overview tab: a click on an entry in the tree shows what it is, where it points and how it logs in.
/// There is only ever one; it follows the selection and comes to the front when an entry is clicked.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The Overview tab while it is open (closing it is fine: the next click opens it again).</summary>
    public OverviewViewModel? Overview { get; private set; }

    /// <summary>A click on an entry in the tree: show its Overview in the focused pane, in front.</summary>
    public void ShowOverview()
    {
        if (Selected is not { } node || node.Id == FavoritesId || node.IsFolder) return;
        if (Overview is { } o && Sessions.Contains(o))
        {
            // Stays where it is, unless the other pane has the focus in split view: then it moves there.
            if (IsSplit && o.Pane != FocusedPane) MoveSession(o, FocusedPane);
            RefreshOverview();
            ActiveSession = o;
            return;
        }
        Overview = new OverviewViewModel { Main = this };
        Overview.CopyTotp = CopyTotpCommand;
        Overview.Closed += s => { ((OverviewViewModel)s).Stop(); if (Overview == s) Overview = null; };
        RefreshOverview();
        AddSession(Overview);
    }

    /// <summary>Fills the Overview from the selected entry. Runs on every selection, presence, Zabbix or Oxidized change.</summary>
    private void RefreshOverview()
    {
        if (Overview is not { } o || _store is null) return;
        if (Selected is not { } node || node.Id == FavoritesId || node.IsRoot)
        {
            if (Selected is null && o.Node is { } shown && _store.Get(shown.Id) is null) CloseSession(o);
            return;
        }
        var e = node.Entry;
        // Same entry again (presence, Zabbix poll): keep shown secrets shown.
        var revealed = o.Node?.Id == node.Id
            ? o.LoginRows.Concat(o.FieldRows).Where(r => r.IsRevealed).Select(r => r.Key).ToHashSet()
            : new HashSet<string>();
        o.Show(node, node.IsFolder ? OverviewViewModel.FolderTag : node.KindTag);
        o.Name = e.Name;
        o.IsFolder = node.IsFolder;
        o.ShowFolderGlyph = node.IsFolder && !node.HasCustomIcon;
        o.IsCredential = e.Kind == EntryKind.Credential;
        o.IsFavorite = IsFavorite(e);
        o.CanConnect = e.IsConnectable;
        o.CanBackups = OxidizedActive && e.IsConnectable;
        o.Description = e.Description?.Trim() ?? "";
        var path = ParentPath(e);
        o.FolderText = path;
        var kind = node.IsFolder ? "Folder" : EntryKindName(e);
        var folder = path.Length == 0 ? "" : path[(path.LastIndexOf('\\') + 1)..];
        o.KindText = folder.Length == 0 ? kind : $"{kind}  ·  {folder}";
        foreach (var t in (e.Tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            o.Tags.Add(t);

        FillChips(o, e);
        FillConnection(o, e, path);
        FillLogin(o, e);
        FillFields(o, e);

        if (e.CreatedBy is { Length: > 0 } cb)
            o.People.Add(Person("Created by", cb, e.CreatedAt));
        if (e.UpdatedBy is { Length: > 0 } ub && e.Revision > 0 && e.UpdatedAt != e.CreatedAt)
            o.People.Add(Person("Modified by", ub, e.UpdatedAt));
        o.HasLogin = o.LoginRows.Count > 0 || o.HasTotp;
        foreach (var r in o.LoginRows.Concat(o.FieldRows))
            if (r.CanReveal && revealed.Contains(r.Key)) r.ToggleRevealCommand.Execute(null);
    }

    private static OverviewPerson Person(string label, string user, DateTimeOffset? at)
    {
        var shortName = user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
        var initials = new string(shortName.Split(['.', ' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0])).Take(2).ToArray());
        if (initials.Length < 2 && shortName.Length >= 2) initials = shortName[..2].ToUpperInvariant();
        return new OverviewPerson(label, shortName, initials, PresenceChip.ColorFor(user), at is { } t ? Ago(t) : "",
            at is { } x ? x.ToLocalTime().ToString("d MMM yyyy HH:mm") : "");
    }

    /// <summary>"just now", "5 minutes ago", "3 days ago", "2 months ago".</summary>
    internal static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        static string N(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return N((int)d.TotalMinutes, "minute");
        if (d.TotalDays < 1) return N((int)d.TotalHours, "hour");
        if (d.TotalDays < 31) return N((int)d.TotalDays, "day");
        if (d.TotalDays < 365) return N((int)(d.TotalDays / 30.4), "month");
        return N((int)(d.TotalDays / 365.25), "year");
    }

    private string ParentPath(Entry e)
    {
        var parts = new List<string>();
        for (var (p, hops) = (e.ParentId, 0); p is { } pid && hops < 64 && _store?.Get(pid) is { } f; p = f.ParentId, hops++)
            parts.Insert(0, f.Name);
        return parts.Count == 0 ? "" : string.Join("\\", parts);
    }

    private static string EntryKindName(Entry e) => e.Kind switch
    {
        EntryKind.Web => "Website",
        EntryKind.Credential => (e.Credential?.Type ?? CredentialType.Password) switch
        {
            CredentialType.SshKey => "Credential · SSH key",
            CredentialType.Certificate => "Credential · certificate",
            CredentialType.Secret => "Credential · secret",
            _ => "Credential",
        },
        _ => KindName(e.Kind),
    };

    private void FillChips(OverviewViewModel o, Entry e)
    {
        // Zabbix: worst state and the first problem, or OK; unlinked shows a link button instead.
        if (ZabbixActive && e.IsConnectable && ZabbixSnapshot is { } snap)
        {
            var h = snap.HealthFor(e);
            if (!h.IsLinked)
                o.Chips.Add(new OverviewChip("zabbix", "Not in Zabbix", "", null, "Link host", LinkToZabbixCommand));
            else
            {
                var tag = TagFor(h.State);
                var n = h.Problems.Count;
                var text = n > 0 ? $"Zabbix: {n} problem{(n == 1 ? "" : "s")}" : h.Summary == "OK" ? "Zabbix OK" : $"Zabbix: {h.Summary}";
                var sub = n > 0 ? $"worst {SeverityName(h.Problems.Max(p => p.Severity)).ToLowerInvariant()}" : "";
                o.Chips.Add(new OverviewChip("zabbix", text, sub, tag ?? "ok", "Zabbix", OpenInZabbixCommand));
            }
        }
        // Oxidized: last config backup and a way to the backups.
        if (SelectedOxidizedVisible)
        {
            if (SelectedOxidizedLinked)
                o.Chips.Add(new OverviewChip("oxidized", SelectedOxidizedText, "", SelectedOxidizedState, "Config backups", OpenConfigBackupsCommand));
            else
                o.Chips.Add(new OverviewChip("oxidized", "No config backups", "", "never", "Link device", LinkToOxidizedCommand));
        }
        // Who else is on it right now.
        foreach (var p in _store!.PresenceFor(e.Id))
        {
            var state = p.State.ToString().ToLowerInvariant();
            o.Chips.Add(new OverviewChip("presence", $"{p.User} is {state}", p.Machine, null, null, null, p.Initials, PresenceChip.ColorFor(p.User)));
        }
    }

    private void FillConnection(OverviewViewModel o, Entry e, string path)
    {
        var rows = o.ConnectionRows;
        o.DetailsTitle = e.Kind == EntryKind.Credential || e.Kind == EntryKind.Folder ? "Details" : "Connection";
        o.ConnectionNote = "";
        var connect = e.IsConnectable ? ConnectCommand : null;

        if (e.Kind == EntryKind.Web)
        {
            var url = e.Web?.Url ?? "";
            var alts = e.Endpoints.Skip(1).Select(ep => SwapHost(url, ep)).ToList();
            rows.Add(new OverviewRow("Website", url) { Style = "link", Alternates = alts, Follow = connect, Open = connect, OpenTip = "Open in a tab", Copy = CopyText(url, "URL"), CopyTip = "Copy URL" });
            if (alts.Count > 0) o.ConnectionNote = "tries the main URL first, then the others in order";
        }
        else if (e.Kind == EntryKind.Serial)
        {
            var s = e.Serial;
            rows.Add(new OverviewRow("Port", s?.PortName ?? "") { Style = "mono", Open = connect, OpenTip = "Open session" });
        }
        else if (e.HasHosts || (e.Kind != EntryKind.Folder && e.Kind != EntryKind.Credential && e.Endpoints.Count > 0))
        {
            var main = e.Endpoints.FirstOrDefault()?.ToString() ?? "";
            var alts = e.Endpoints.Skip(1).Select(x => x.ToString()).ToList();
            if (main.Length > 0)
                rows.Add(new OverviewRow("Host", main)
                {
                    Badge = alts.Count > 0 ? "main" : null, Style = "mono", Alternates = alts,
                    Open = connect, OpenTip = "Open session", Copy = CopyText(e.Endpoints[0].Host, "Host"), CopyTip = "Copy host",
                });
            if (alts.Count > 0) o.ConnectionNote = "tries the main host first, then the others in order";
        }

        if (path.Length > 0) rows.Add(new OverviewRow("Folder", path) { Style = "dim" });

        if (e.Kind == EntryKind.Credential)
        {
            var usedBy = _store!.Entries.Where(x => !x.Deleted && x.Login == LoginSource.Saved && x.CredentialId == e.Id).Select(x => x.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            rows.Add(new OverviewRow("Used by", usedBy.Count == 0 ? "nothing yet (Ctrl+click it with an entry selected to link it)"
                : usedBy.Count <= 4 ? string.Join(", ", usedBy) : $"{usedBy.Count} entries: {string.Join(", ", usedBy.Take(3))} and {usedBy.Count - 3} more") { Style = "dim" });
            if (e.Credential?.PrivateKeyPath is { Length: > 0 } kp) rows.Add(new OverviewRow("Key file", kp) { Style = "mono", Copy = CopyText(kp, "Key path") });
        }

        if (ProtocolSummary(e) is { Length: > 0 } summary)
            rows.Add(new OverviewRow(e.Kind == EntryKind.Web ? "Browser" : KindName(e.Kind), summary) { Style = "dim" });
        if (e.Kind == EntryKind.Rdp && e.Rdp?.SshTunnelId is { } tid)
            rows.Add(new OverviewRow("Tunnel", _store!.Get(tid)?.Name ?? "missing SSH entry") { Style = "dim" });
        if (e.Connection is { WakeOnLan: true } wol)
            rows.Add(new OverviewRow("Wake-on-LAN", wol.MacAddress ?? "on") { Style = "mono", Copy = wol.MacAddress is { } mac ? CopyText(mac, "MAC address") : null });
    }

    /// <summary>The url with its host (and port) swapped for a fallback endpoint.</summary>
    private static string SwapHost(string url, HostEndpoint ep)
    {
        if (!Uri.TryCreate(WebSettings.NormalizeUrl(url), UriKind.Absolute, out var u)) return ep.ToString();
        var b = new UriBuilder(u) { Host = ep.Host };
        if (ep.Port > 0) b.Port = ep.Port;
        return b.Uri.ToString();
    }

    private string ProtocolSummary(Entry e)
    {
        var parts = new List<string>();
        switch (e.Kind)
        {
            case EntryKind.Ssh:
                if (e.Ssh?.JumpHostId is { } jid) parts.Add($"via {_store!.Get(jid)?.Name ?? "missing jump host"}");
                parts.Add(e.Ssh?.AutoAcceptHostKey != false ? "host key auto-accepted" : "strict host key");
                parts.Add(e.Ssh?.AllowLegacyAlgorithms != false ? "legacy algorithms allowed" : "modern algorithms only");
                if (e.Ssh?.Forwards is { Count: > 0 } fw && fw.Any(f => f.Enabled)) parts.Add($"{fw.Count(f => f.Enabled)} port forward{(fw.Count(f => f.Enabled) == 1 ? "" : "s")}");
                break;
            case EntryKind.Rdp:
                parts.Add(e.Rdp is { DesktopWidth: > 0 } r ? $"{r.DesktopWidth}×{r.DesktopHeight}" : "fit to tab");
                if (e.Rdp?.AdminSession == true) parts.Add("admin session");
                if (e.Rdp?.UseRdGateway == true) parts.Add($"gateway {e.Rdp.GatewayHost}");
                parts.Add(e.Rdp?.IgnoreCertificateErrors != false ? "certificate warnings skipped" : "certificate prompt");
                break;
            case EntryKind.Web:
                parts.Add((e.Web?.Browser ?? BrowserKind.Embedded) == BrowserKind.Embedded ? "built-in tab" : (e.Web?.Browser ?? BrowserKind.Embedded).ToString());
                if (e.Web?.IgnoreCertificateErrors != false) parts.Add("certificate errors skipped");
                if (e.Web?.AutoFill == true) parts.Add("fills the login form");
                break;
        }
        if (e.IsConnectable && e.OpenIn != OpenMode.Tab) parts.Add(e.OpenIn == OpenMode.Window ? "opens in its own window" : "opens full screen");
        return string.Join(" · ", parts);
    }

    private void FillLogin(OverviewViewModel o, Entry e)
    {
        if (_store is null || _protector is null || e.Kind == EntryKind.Folder && e.Login == LoginSource.Inherited) return;
        var rows = o.LoginRows;
        var (src, holder) = _store.ResolveLogin(e);
        var inherited = e.Kind != EntryKind.Credential && e.Login == LoginSource.Inherited && src != LoginSource.None;

        if (e.Kind != EntryKind.Credential)
        {
            switch (src)
            {
                case LoginSource.Saved when holder is not null:
                    var hid = holder.Id;
                    var where = ParentPath(holder);
                    rows.Add(new OverviewRow("Credentials", (where.Length > 0 ? where + "\\" : "") + holder.Name)
                    {
                        Badge = inherited ? "linked, from folder" : "linked", Style = "link",
                        Follow = new RelayCommand(() => { RevealEntry(hid); ShowOverview(); }),
                    });
                    break;
                case LoginSource.WindowsLogin:
                    rows.Add(new OverviewRow("Credentials", "Your Windows login") { Style = "dim", Badge = inherited ? "from folder" : null });
                    return;
                case LoginSource.Embedded:
                    if (inherited) rows.Add(new OverviewRow("Credentials", "Typed in on the folder") { Style = "dim", Badge = "from folder" });
                    break;
                default:
                    if (e.IsConnectable) rows.Add(new OverviewRow("Credentials", "None. Hold Ctrl and click a credential to link it.") { Style = "dim" });
                    return;
            }
        }

        RemoteMan.Protocols.ResolvedCredential c;
        try { c = Secrets.Resolve(e, _store, _protector); }
        catch (Exception ex) { rows.Add(new OverviewRow("Login", $"Couldn't read it: {ex.Message}") { Style = "dim" }); return; }

        var user = c.Domain is { Length: > 0 } d ? $"{d}\\{c.UserName}" : c.UserName ?? "";
        if (user.Length > 0)
            rows.Add(new OverviewRow("Username", user) { Copy = CopyText(user, "User name"), CopyTip = "Copy username" });
        var id = e.Id;
        if (c.AskPassword)
            rows.Add(new OverviewRow("Password", "Asked for when connecting") { Style = "dim" });
        else if (c.Password is { Length: > 0 })
            rows.Add(new OverviewRow("Password", OverviewRow.Mask)
            {
                Style = "secret", Reveal = () => ResolveFor(id)?.Password,
                Copy = CopyPasswordCommand, CopyTip = "Copy password (clears from the clipboard)",
            });
        if (c.PrivateKey is { Length: > 0 }) rows.Add(new OverviewRow("SSH key", "stored in the credential") { Style = "dim" });
        if (c.TotpSecret is { Length: > 0 }) o.SetTotp(() => ResolveFor(id)?.TotpSecret);
    }

    private RemoteMan.Protocols.ResolvedCredential? ResolveFor(Guid id)
    {
        if (_store?.Get(id) is not { } e || _protector is null) return null;
        try { return Secrets.Resolve(e, _store, _protector); }
        catch (Exception) { return null; }
    }

    private void FillFields(OverviewViewModel o, Entry e)
    {
        foreach (var f in e.Fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(f.Name)) continue;
            var field = f;
            var copy = f.Value is null ? null : new RelayCommand(() => CopyField(field));
            if (f.Secret)
                o.FieldRows.Add(new OverviewRow(f.Name, f.Value is null ? "" : OverviewRow.Mask)
                {
                    Style = "secret", Copy = copy, CopyTip = $"Copy {f.Name}",
                    Reveal = f.Value is null ? null : () => { try { return _protector?.Unprotect(field.Value!); } catch (Exception) { return "(can't decrypt)"; } },
                });
            else
                o.FieldRows.Add(new OverviewRow(f.Name, f.Value ?? "") { Copy = copy, CopyTip = $"Copy {f.Name}" });
        }
    }

    private RelayCommand CopyText(string text, string what) => new(() =>
    {
        CredentialClipboard.CopyText(text, secret: false);
        Message = $"{what} copied";
    });
}
