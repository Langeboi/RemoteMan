using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// Several entries selected at once in the tree (Ctrl+click toggles one, Shift+click takes a range),
/// so they can be moved to a folder together. <see cref="Selected"/> stays the one focused entry that every
/// other command acts on; the multi-selection is the extra set the tree highlights and Move works on.
/// </summary>
public sealed partial class MainViewModel
{
    // Insertion order is kept so "Moved 3 entries" messages and the anchor behave predictably.
    private readonly List<Guid> _multi = new();

    /// <summary>The entries picked with Ctrl or Shift (includes the focused one). Empty when only one entry is selected.</summary>
    public IReadOnlyList<Guid> MultiSelection => _multi;
    public bool HasMultiSelection => _multi.Count > 1;
    public bool IsInMultiSelection(EntryNode node) => HasMultiSelection && _multi.Contains(node.Id);

    private static bool CanMultiSelect(EntryNode? n) => n is { IsRoot: false } && n.Id != FavoritesId;

    /// <summary>Ctrl+click: adds or removes one entry. The focused entry joins the set the first time.</summary>
    public void ToggleMultiSelect(EntryNode node)
    {
        if (!CanMultiSelect(node)) return;
        // Starting a new set: it begins with the focused entry (or, with nothing focused, the clicked one becomes it).
        if (!HasMultiSelection)
        {
            _multi.Clear();
            if (CanMultiSelect(Selected)) _multi.Add(Selected!.Id);
            else { node.IsSelected = true; _multi.Add(node.Id); ReapplyMultiSelection(); return; }
        }
        if (!_multi.Remove(node.Id)) _multi.Add(node.Id);
        // Un-picking the focused entry hands the focus to the last one still picked, so the highlight matches what moves.
        else if (node == Selected && _multi.Count > 0 && _nodes.TryGetValue(_multi[^1], out var next)) next.IsSelected = true;
        ReapplyMultiSelection();
    }

    /// <summary>Shift+click: everything visible in the tree between the focused entry and <paramref name="node"/>.</summary>
    public void RangeSelect(EntryNode node, bool additive = false)
    {
        if (!CanMultiSelect(node)) return;
        if (!CanMultiSelect(Selected)) { node.IsSelected = true; return; }
        var anchor = Selected!;
        var visible = VisibleNodes().ToList();
        int a = visible.IndexOf(anchor), b = visible.IndexOf(node);
        if (a < 0 || b < 0) { ToggleMultiSelect(node); return; }
        if (a > b) (a, b) = (b, a);
        // Ctrl+Shift adds the range to what is already picked; plain Shift replaces it.
        if (!additive) _multi.Clear();
        foreach (var n in visible.Skip(a).Take(b - a + 1))
            if (CanMultiSelect(n) && !_multi.Contains(n.Id)) _multi.Add(n.Id);
        ReapplyMultiSelection();
    }

    public void ClearMultiSelection()
    {
        if (_multi.Count == 0) return;
        _multi.Clear();
        ReapplyMultiSelection();
    }

    /// <summary>Rows in the order they show, skipping what sits inside a closed folder.</summary>
    private IEnumerable<EntryNode> VisibleNodes()
    {
        IEnumerable<EntryNode> Walk(IEnumerable<EntryNode> nodes)
        {
            foreach (var n in nodes)
            {
                yield return n;
                if (n.IsExpanded) foreach (var c in Walk(n.Children)) yield return c;
            }
        }
        return Walk(Roots);
    }

    /// <summary>Puts the highlight on the picked rows; also run after the tree is rebuilt, dropping entries that are gone.</summary>
    private void ReapplyMultiSelection()
    {
        if (_store is not null) _multi.RemoveAll(id => _store.Get(id) is null);
        var on = _multi.Count > 1 ? _multi.ToHashSet() : new HashSet<Guid>();
        foreach (var n in _nodes.Values) n.IsMultiSelected = on.Contains(n.Id);
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    /// <summary>The ids a move acts on: the multi-selection when <paramref name="node"/> is part of it, otherwise just that entry.</summary>
    public IReadOnlyList<Guid> MoveSetFor(EntryNode node) => IsInMultiSelection(node) ? _multi.ToList() : new List<Guid> { node.Id };

    /// <summary>True when at least one of <paramref name="ids"/> can go into <paramref name="folder"/>.</summary>
    public bool CanMoveAny(IReadOnlyList<Guid> ids, Guid? folder) =>
        _store is not null && _store.MovableForMove(ids, folder).Count > 0;

    /// <summary>Moves several entries into <paramref name="folder"/> (null = top level). A selected folder takes its
    /// contents with it, so selected entries inside a selected folder stay where they are inside it.</summary>
    public async Task MoveManyAsync(IReadOnlyList<Guid> ids, Guid? folder)
    {
        if (_store is null) return;
        if (ids.Count == 1) { await MoveAsync(ids[0], folder); return; }
        if (!_store.IsOnline) { Message = "Offline: entries can't be moved until the database is back"; return; }
        var moving = _store.MovableForMove(ids, folder);
        if (moving.Count == 0) return;
        var moved = 0;
        try
        {
            foreach (var e in moving)
            {
                var copy = EntryStore.Clone(e);
                copy.ParentId = folder;
                await _store.SaveAsync(copy);
                moved++;
            }
            var where = folder is { } f && _store.Get(f) is { } fe ? fe.Name : "the top level";
            Message = $"Moved {moved} {(moved == 1 ? "entry" : "entries")} to {where}";
            if (folder is { } fid && _nodes.TryGetValue(fid, out var fn)) fn.IsExpanded = true;
        }
        catch (Exception ex) { Message = moved > 0 ? $"Moved {moved} of {moving.Count}, then: {ex.Message}" : ex.Message; }
    }
}
