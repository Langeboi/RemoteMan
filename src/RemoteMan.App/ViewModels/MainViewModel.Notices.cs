using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>A card in the bottom-right corner explaining why a connection failed (or fell back).</summary>
public sealed partial class Notice : ObservableObject
{
    public Notice(ConnectionProblem problem, Entry? entry)
    {
        Problem = problem;
        Entry = entry;
        Created = Shown = DateTimeOffset.Now;
    }

    /// <summary>When the fade countdown started (reset when the mouse leaves the card).</summary>
    public DateTimeOffset Shown { get; set; }

    public ConnectionProblem Problem { get; }
    public Entry? Entry { get; }
    public DateTimeOffset Created { get; }
    public string Title => Entry is null ? Problem.Title : $"{Entry.Name}: {char.ToLowerInvariant(Problem.Title[0])}{Problem.Title[1..]}";
    public string KindTag => Entry is null ? "" : EntryNode.KindTagFor(Entry.Kind);
    public bool IsWarning => Problem.IsWarning;
    public bool HasHint => !string.IsNullOrEmpty(Problem.Hint);
    public bool HasDetails => !string.IsNullOrEmpty(Problem.Details);
    public bool CanRetry => Entry is { IsConnectable: true } && !Problem.IsWarning;
    public bool CanEdit => Entry is not null;
    [ObservableProperty] private bool _isHovered;
    [ObservableProperty] private bool _isFaded;
    [ObservableProperty] private string _age = "just now";
}

public partial class MainViewModel
{
    public ObservableCollection<Notice> Notices { get; } = new();
    [ObservableProperty] private bool _hasNotices;
    private DispatcherTimer? _noticeTimer;
    private static readonly TimeSpan NoticeLife = TimeSpan.FromSeconds(10);

    /// <summary>Shows a notice for a failed (or fallback) connection. Keeps the newest four.</summary>
    public void Notify(ConnectionProblem problem, Entry? entry)
    {
        OnUi(() =>
        {
            // Same entry, same problem: replace instead of stacking duplicates.
            foreach (var old in Notices.Where(n => n.Entry?.Id == entry?.Id && n.Problem.Title == problem.Title).ToList()) Notices.Remove(old);
            Notices.Add(new Notice(problem, entry));
            while (Notices.Count > 4) Notices.RemoveAt(0);
            HasNotices = true;
            _noticeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => AgeNotices(), System.Windows.Application.Current.Dispatcher);
            _noticeTimer.Start();
        });
    }

    private void AgeNotices()
    {
        var now = DateTimeOffset.Now;
        foreach (var n in Notices.ToList())
        {
            var created = now - n.Created;
            n.Age = created.TotalMinutes >= 1 ? $"{(int)created.TotalMinutes} min ago" : "just now";
            if (n.IsHovered) continue;
            var age = now - n.Shown;
            // Fade, then go. Hovering keeps it; it starts over when the mouse leaves.
            if (age > NoticeLife + TimeSpan.FromSeconds(2)) Notices.Remove(n);
            else n.IsFaded = age > NoticeLife;
        }
        HasNotices = Notices.Count > 0;
        if (!HasNotices) _noticeTimer?.Stop();
    }

    [RelayCommand]
    private void DismissNotice(Notice n)
    {
        Notices.Remove(n);
        HasNotices = Notices.Count > 0;
    }

    [RelayCommand]
    private async Task RetryNotice(Notice n)
    {
        DismissNotice(n);
        if (n.Entry is { } e) await ConnectEntryAsync(_store?.Get(e.Id) ?? e);
    }

    [RelayCommand]
    private async Task EditNoticeEntry(Notice n)
    {
        DismissNotice(n);
        if (n.Entry is not { } e || _store?.Get(e.Id) is not { } current) return;
        if (_nodes.TryGetValue(e.Id, out var node)) Selected = node;
        await EditAndSaveAsync(EntryStore.Clone(current));
    }

    [RelayCommand]
    private void NoticeDetails(Notice n)
    {
        n.IsHovered = true;
        var text = string.Join("\n\n", new[] { n.Problem.Why, n.Problem.Hint, n.Problem.Details }.Where(x => !string.IsNullOrEmpty(x)));
        Choose?.Invoke(n.Title, text, ["Close"]);
        n.IsHovered = false;
    }

    /// <summary>Hooks a new tab so failures inside it (sign-in, disconnect) show as notices.</summary>
    private void TrackFailures(SessionViewModel s) => s.Failed += p => Notify(p, s.Entry);
}
