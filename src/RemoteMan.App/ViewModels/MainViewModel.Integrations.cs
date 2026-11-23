using RemoteMan.App.Services;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>The Zabbix and Oxidized settings of the open data source: loaded on open, reloaded when someone changes them.</summary>
public sealed partial class MainViewModel
{
    private IntegrationSettings? _integrations;
    private static readonly ZabbixConfig NoZabbix = new();
    private static readonly OxidizedConfig NoOxidized = new();
    private bool _reloadingIntegrations;

    /// <summary>The open data source's integration settings (null until it has opened).</summary>
    public IntegrationSettings? Integrations => _integrations;
    public ZabbixConfig ZabbixCfg => _integrations?.Zabbix ?? NoZabbix;
    public OxidizedConfig OxidizedCfg => _integrations?.Oxidized ?? NoOxidized;

    private async Task LoadIntegrationsAsync(DataSourceConfig cfg, EntryStore store)
    {
        if (_protector is not null)
        {
            var ints = _integrations = new IntegrationSettings(_settings, cfg, store, _protector);
            try
            {
                if (await ints.LoadAsync(what => AskToShareIntegrations(cfg.Name, what)) is { } note) Message = note;
            }
            catch (Exception ex) { Message = $"Couldn't read the Zabbix and Oxidized settings: {ex.Message}"; }
            // An admin saved new settings, or the database came back after an offline start.
            store.SettingsChanged += (_, _) => OnUi(() => _ = ReloadIntegrationsAsync(announce: true));
            store.OnlineChanged += (_, up) => { if (up) OnUi(() => _ = ReloadIntegrationsAsync(announce: false)); };
        }
        await StartZabbixAsync();
        await StartOxidizedAsync();
    }

    /// <summary>True: share them; false: keep them on this PC; null (window closed): ask again next time.</summary>
    private bool? AskToShareIntegrations(string source, string what)
    {
        if (Choose is null) return null;
        var pick = Choose("Share integration settings?",
            $"Zabbix and Oxidized settings are now kept in the data source, so everyone on \"{source}\" uses the same ones. " +
            $"This PC has {what} from before, and \"{source}\" has none yet.\n\n" +
            "Save them there for everyone? The token and password are encrypted with the data source's vault passphrase.",
            ["Share them", "Keep on this PC only"]);
        return pick switch { 0 => true, 1 => false, _ => null };
    }

    /// <summary>Reads the settings again and restarts Zabbix or Oxidized only when theirs changed.</summary>
    private async Task ReloadIntegrationsAsync(bool announce)
    {
        if (_integrations is not { } ints || _reloadingIntegrations) return;
        _reloadingIntegrations = true;
        try
        {
            var (z, o) = (ints.ZabbixStamp, ints.OxidizedStamp);
            if (await ints.LoadAsync(null) is { } note) Message = note;
            if (ints.ZabbixStamp != z)
            {
                await StartZabbixAsync();
                if (announce) Message = $"Zabbix settings were changed for everyone{(ints.ZabbixRow?.UpdatedBy is { } by ? $" by {by}" : "")}";
            }
            if (ints.OxidizedStamp != o)
            {
                await StartOxidizedAsync();
                if (announce) Message = $"Oxidized settings were changed for everyone{(ints.OxidizedRow?.UpdatedBy is { } by ? $" by {by}" : "")}";
            }
        }
        catch (Exception) { /* the next change signal tries again */ }
        finally { _reloadingIntegrations = false; }
    }
}
