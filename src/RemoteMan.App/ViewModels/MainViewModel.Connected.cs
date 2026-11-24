namespace RemoteMan.App.ViewModels;

/// <summary>"You're connected": entries with a session of yours open look live in the tree (green dot on the icon, bold name),
/// and teammates on a shared database see you on every one of them.</summary>
public sealed partial class MainViewModel
{
    private bool _trackingConnections;

    private void StartTrackingConnections()
    {
        if (_trackingConnections) return;
        _trackingConnections = true;
        Sessions.CollectionChanged += (_, _) => RefreshMyConnections();
    }

    /// <summary>Open sessions that are a real connection to an entry: tabs and sessions in their own window.</summary>
    private IEnumerable<SessionViewModel> MyConnections(SessionViewModel? closing = null) =>
        Sessions.Concat(_windowSessions)
            .Where(s => s != closing && s.IsConnection && s is not ConfigBackupsSessionViewModel and not SharedSessionViewModel);

    /// <summary>Recount after a tab or window opened or closed: marks the tree and tells the database.</summary>
    private void RefreshMyConnections(SessionViewModel? closing = null)
    {
        ApplyMyConnections(closing);
        if (_store is not null)
        {
            // Only this data source's entries: tabs still open from another one aren't announced here.
            var store = _store;
            _ = store.SetMyConnectionsAsync(MyConnections(closing).Where(s => s.Entry.Revision > 0 && store.Get(s.Entry.Id) is not null).Select(s => s.Entry.Id).ToList());
        }
    }

    /// <summary>Sets each tree row's count of open sessions (several tabs to one entry count each).</summary>
    private void ApplyMyConnections(SessionViewModel? closing = null)
    {
        var counts = MyConnections(closing).GroupBy(s => s.Entry.Id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var n in _nodes.Values)
            n.MyTabs = n.IsFolder || n.IsRoot ? 0 : counts.GetValueOrDefault(n.Id);
    }
}
