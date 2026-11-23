using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// Oxidized's device list from the RemoteMan server: every SSH entry. The server reads names and addresses from the
/// database itself; what it can't read (logins, which are encrypted with the vault key) an admin's RemoteMan seals
/// for the server here, together with the Oxidized names of entries that already matched a device.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _feedLock = new(1, 1);
    private string? _feedNamesStamp;

    /// <summary>Opens the Oxidized device list window.</summary>
    public Action? OxidizedFeedDialog { get; set; }

    [RelayCommand]
    private void OxidizedDevices() => OxidizedFeedDialog?.Invoke();

    /// <summary>Whether this PC keeps the server's copy of the logins and names up to date (admins, server with a feed).</summary>
    public bool CanSyncOxidizedFeed => _integrations is { CanChange: true, OxidizedFeedInfo.PublicKey: { Length: > 0 } } && _store is not null && _protector is not null;

    public OxidizedFeedSync ReadOxidizedFeedSync()
    {
        if (_integrations?.OxidizedSyncRow is { } row)
        {
            try { return JsonSerializer.Deserialize<OxidizedFeedSync>(row.Value, OxidizedFeed.Json) ?? new(); }
            catch (JsonException) { }
        }
        return new();
    }

    /// <summary>
    /// Seals every SSH entry's login for the server and saves the names of matched entries. Writes only when something
    /// changed. Runs after each Oxidized refresh and after the device list window saves. Returns a status line.
    /// </summary>
    public async Task<string?> SyncOxidizedFeedAsync(CancellationToken ct = default)
    {
        if (!CanSyncOxidizedFeed || _integrations is not { } ints || _store is not { } store || _protector is not { } protector) return null;
        if (!await _feedLock.WaitAsync(0, ct)) return null;
        try
        {
            var cfg = ints.Oxidized;
            var pub = ints.OxidizedFeedInfo!.PublicKey!;
            var before = ints.OxidizedSyncRow?.Value;
            var sync = ReadOxidizedFeedSync();

            byte[]? hmacKey = null;
            if (sync.Key is { Length: > 0 })
            {
                try { hmacKey = Convert.FromBase64String(protector.Unprotect(sync.Key)); }
                catch (Exception) { hmacKey = null; } // another vault passphrase: start over
            }
            if (hmacKey is null)
            {
                hmacKey = RandomNumberGenerator.GetBytes(32);
                sync.Key = protector.Protect(Convert.ToBase64String(hmacKey));
                sync.Logins.Clear();
            }

            var entries = store.Entries.ToList();
            var ssh = entries.Where(e => e.Kind == EntryKind.Ssh && !e.Deleted).ToList();

            // Logins
            var logins = new Dictionary<string, OxidizedSealedLogin>();
            if (cfg.FeedSendLogins)
            {
                var choice = sync.Choice(cfg.FeedAutoAdd);
                foreach (var e in ssh.Where(e => choice.IsChosen(e.Id)))
                {
                    string json;
                    try
                    {
                        var c = Secrets.Resolve(e, store, protector);
                        var fields = Secrets.FieldValues(e, protector);
                        var enable = OxidizedFeed.EnableFieldNames.Select(n => fields.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
                        if (string.IsNullOrEmpty(c.UserName) && string.IsNullOrEmpty(c.Password)) continue;
                        json = OxidizedFeed.LoginJson(c.UserName, c.Password, enable);
                    }
                    catch (Exception) { continue; } // a secret from another vault key
                    var id = OxidizedFeed.IdKey(e.Id);
                    var fp = OxidizedFeed.Fingerprint(hmacKey, pub, id, json);
                    logins[id] = sync.Logins.TryGetValue(id, out var old) && old.F == fp
                        ? old
                        : new OxidizedSealedLogin { F = fp, S = OxidizedFeed.Seal(pub, id, json) };
                }
            }
            sync.Logins = logins;

            // Names of entries Oxidized already has under another name, so their history carries on.
            if (_oxSnapshot is { } snap)
            {
                var links = new Dictionary<string, string>(sync.Links.Where(kv => ssh.Any(e => OxidizedFeed.IdKey(e.Id) == kv.Key)));
                foreach (var e in ssh)
                {
                    var id = OxidizedFeed.IdKey(e.Id);
                    if (!string.IsNullOrEmpty(e.OxidizedNode)) { links.Remove(id); continue; }
                    if (links.TryGetValue(id, out var saved) && snap.Matcher.Get(saved) is not null) continue;
                    if (snap.Matcher.Match(e) is { } m && m.Node.FullName != DefaultFeedName(e, entries, cfg.FeedOptions)) links[id] = m.Node.FullName;
                    else if (saved is not null && snap.Nodes.Count > 0) links.Remove(id);
                }
                sync.Links = links;
            }

            var after = JsonSerializer.Serialize(sync, OxidizedFeed.Json);
            string? note = null;
            if (after != before)
            {
                await ints.SaveOxidizedSyncAsync(after, ct);
                note = "Oxidized device list updated";
            }

            // New or renamed devices: ask Oxidized to read the list again now instead of at its next reload.
            var plan = OxidizedFeed.Plan(entries, cfg.FeedOptions, sync.Links, _oxSnapshot, sync.Choice(cfg.FeedAutoAdd));
            var stamp = string.Join("\n", plan.Where(i => i.State is OxidizedFeedState.InOxidized or OxidizedFeedState.Missing).Select(i => $"{i.FullName}|{i.Ip}|{i.Port}|{i.Model}"));
            if (_feedNamesStamp is not null && stamp != _feedNamesStamp && OxidizedActive)
            {
                try
                {
                    using var client = await ConnectOxidizedAsync(ct);
                    await client.ReloadAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            _feedNamesStamp = stamp;
            return note;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return $"Oxidized device list: {ex.Message}"; }
        finally { _feedLock.Release(); }
    }

    /// <summary>The name the server gives an entry that has no saved name.</summary>
    private static string DefaultFeedName(Entry e, List<Entry> entries, OxidizedFeedOptions options)
    {
        var copy = RemoteMan.Core.Storage.EntryStore.Clone(e);
        copy.OxidizedNode = null;
        var list = entries.Where(x => x.Kind == EntryKind.Folder).Append(copy);
        return OxidizedFeed.Plan(list, options, null, null).FirstOrDefault()?.FullName ?? e.Name;
    }

    private async Task RunOxidizedFeedSyncAsync()
    {
        if (await SyncOxidizedFeedAsync() is { } note) OnUi(() => Message = note);
    }

    /// <summary>
    /// Saves which entries go to Oxidized (unticked ones in <paramref name="excluded"/>, ticked ones in <paramref name="included"/>)
    /// and which Oxidized-only devices stay in its list, then brings the logins up to date.
    /// </summary>
    public async Task SaveOxidizedChoiceAsync(IEnumerable<Guid> excluded, IEnumerable<Guid> included, IEnumerable<OxidizedKeptNode> kept)
    {
        if (_integrations is not { } ints) return;
        var sync = ReadOxidizedFeedSync();
        sync.Excluded = excluded.Select(OxidizedFeed.IdKey).Order(StringComparer.Ordinal).ToList();
        sync.Included = included.Select(OxidizedFeed.IdKey).Order(StringComparer.Ordinal).ToList();
        sync.Keep = kept.ToList();
        var json = JsonSerializer.Serialize(sync, OxidizedFeed.Json);
        if (json != ints.OxidizedSyncRow?.Value) await ints.SaveOxidizedSyncAsync(json);
    }
}
