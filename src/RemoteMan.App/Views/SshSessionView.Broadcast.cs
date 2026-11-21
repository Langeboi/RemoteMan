using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>Broadcast typing: what you type in this tab also goes to the other SSH tabs in the group.</summary>
public partial class SshSessionView
{
    /// <summary>One SSH tab in the broadcast bar; ticking it adds the tab to the group.</summary>
    public sealed partial class BroadcastTarget(SshSessionViewModel session, MainViewModel main) : ObservableObject
    {
        public SshSessionViewModel Session { get; } = session;
        public string Title => Session.Title;
        public bool IsConnected => Session.IsConnected;
        [ObservableProperty] private bool _isChecked;
        partial void OnIsCheckedChanged(bool value) { if (!Syncing) main.SetInBroadcast(Session, value); }
        internal bool Syncing { get; set; }
    }

    private MainViewModel? _bmain;
    private readonly ObservableCollection<BroadcastTarget> _btargets = new();
    private bool _bsyncing;
    private DateTime _lastEsc;

    private void HookBroadcast()
    {
        _bmain = Window.GetWindow(this)?.DataContext as MainViewModel ?? Application.Current?.MainWindow?.DataContext as MainViewModel;
        if (_bmain is null) { BroadcastToggle.Visibility = Visibility.Collapsed; return; }
        if (_vm?.Entry.Kind != RemoteMan.Core.Model.EntryKind.Ssh) BroadcastToggle.Visibility = Visibility.Collapsed;
        BroadcastList.ItemsSource = _btargets;
        _bmain.BroadcastChanged += OnBroadcastChanged;
        _bmain.Sessions.CollectionChanged += OnBroadcastSessions;
        RefreshBroadcast();
    }

    private void UnhookBroadcast()
    {
        if (_bmain is null) return;
        _bmain.BroadcastChanged -= OnBroadcastChanged;
        _bmain.Sessions.CollectionChanged -= OnBroadcastSessions;
        if (_vm is not null) _bmain.SetInBroadcast(_vm, false);
    }

    private void OnBroadcastChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(RefreshBroadcast));
    private void OnBroadcastSessions(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(RefreshBroadcast));

    /// <summary>Bar shown while this tab is in the group; one tick box per open SSH tab.</summary>
    private void RefreshBroadcast()
    {
        if (_bmain is null || _vm is null) return;
        var inGroup = _bmain.BroadcastOn && _bmain.InBroadcast(_vm);
        BroadcastBar.Visibility = inGroup ? Visibility.Visible : Visibility.Collapsed;
        _bsyncing = true;
        BroadcastToggle.IsChecked = inGroup;
        _bsyncing = false;
        BroadcastToggle.Foreground = (System.Windows.Media.Brush)FindResource(inGroup ? "Warn" : "Muted");
        if (!inGroup) return;

        var open = _bmain.SshSessions.Where(s => s.Entry.Kind == RemoteMan.Core.Model.EntryKind.Ssh).ToList();
        foreach (var t in _btargets.Where(t => !open.Contains(t.Session)).ToList()) _btargets.Remove(t);
        foreach (var s in open.Where(s => _btargets.All(t => t.Session != s))) _btargets.Add(new BroadcastTarget(s, _bmain));
        foreach (var t in _btargets)
        {
            t.Syncing = true;
            t.IsChecked = _bmain.InBroadcast(t.Session);
            t.Syncing = false;
        }
        var n = _bmain.BroadcastTargets.Count;
        BroadcastLabel.Text = $"Broadcasting to {n} session{(n == 1 ? "" : "s")}";
    }

    private void OnBroadcastToggled(object sender, RoutedEventArgs e)
    {
        if (_bsyncing || _bmain is null || _vm is null) return;
        if (BroadcastToggle.IsChecked == true) _bmain.StartBroadcast(_vm);
        else _bmain.StopBroadcast();
    }

    private void ToggleBroadcast()
    {
        if (_bmain is null || _vm is null || BroadcastToggle.Visibility != Visibility.Visible) return;
        if (_bmain.BroadcastOn && _bmain.InBroadcast(_vm)) _bmain.StopBroadcast();
        else _bmain.StartBroadcast(_vm);
    }

    private void OnBroadcastAll(object sender, RoutedEventArgs e) => _bmain?.AddAllToBroadcast();
    private void OnBroadcastStop(object sender, RoutedEventArgs e) => _bmain?.StopBroadcast();

    /// <summary>
    /// Keys and pastes from the terminal. While broadcasting they go to every session in the group (this one included);
    /// a paste of several lines into several sessions asks first. Esc twice within half a second stops broadcasting.
    /// </summary>
    private void SendInput(string data)
    {
        if (_bmain is null || _vm is null || !_bmain.BroadcastOn || !_bmain.InBroadcast(_vm))
        {
            _chan?.Send(data);
            return;
        }
        if (data == "\x1b")
        {
            var now = DateTime.UtcNow;
            if ((now - _lastEsc).TotalMilliseconds < 500) { _lastEsc = default; _bmain.StopBroadcast(); _chan?.Send(data); return; }
            _lastEsc = now;
        }
        var targets = _bmain.BroadcastTargets;
        var lines = data.Split('\r', StringSplitOptions.RemoveEmptyEntries).Length;
        if (targets.Count > 1 && data.Length > 1 && lines > 1)
        {
            var preview = string.Join("\n", data.Split('\r').Take(6)) + (lines > 6 ? "\n…" : "");
            if (!ChoiceDialog.Confirm(Window.GetWindow(this), $"Paste {lines} lines into {targets.Count} sessions?", preview, "Paste to all")) return;
        }
        _bmain.BroadcastInput(_vm, data);
    }
}
