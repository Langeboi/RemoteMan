using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// Several entries selected at once in the tree (Ctrl+click toggles one, Shift+click or Shift+arrow keys take a range),
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

    // Explorer-style range ends. The anchor is where a Shift range starts and stays put while Shift is held;
    // the cursor is the end that Shift+arrow keys move. A plain selection (click, arrow keys, search) resets both
    // to that entry; Ctrl+click moves both to the toggled entry; Shift+click and Shift+arrows only move the cursor.
    private Guid? _rangeAnchor;
    private Guid? _rangeCursor;
    private bool _movingRangeFocus;

    /// <summary>Called when the focused entry changes: anything but a Shift range move starts a new anchor there.</summary>
    private void ResetRangeAnchor(EntryNode? value)
    {
        if (_movingRangeFocus) return;
        _rangeAnchor = _rangeCursor = CanMultiSelect(value) ? value!.Id : null;
    }

    /// <summary>The anchor if it is still a visible, pickable row, otherwise the focused entry.</summary>
    private EntryNode? RangeAnchor(IReadOnlyList<EntryNode> visible)
    {
        if (_rangeAnchor is { } a && _nodes.TryGetValue(a, out var n) && CanMultiSelect(n) && visible.Contains(n)) return n;
        return CanMultiSelect(Selected) && visible.Contains(Selected!) ? Selected : null;
    }

    /// <summary>
    /// Shift+Up/Down (<paramref name="step"/> -1/+1) or Shift+Home/End (int.MinValue/int.MaxValue): moves the range's
    /// cursor end to the next pickable row and selects everything from the anchor to it. Returns the row to focus,
    /// or null when there is nothing to move to (the caller then leaves the key alone).
    /// </summary>
    public EntryNode? ExtendRangeByKey(int step)
    {
        var visible = VisibleNodes().ToList();
        if (RangeAnchor(visible) is not { } anchor) return null;
        var cursor = _rangeCursor is { } c && _nodes.TryGetValue(c, out var cn) && CanMultiSelect(cn) && visible.Contains(cn) ? cn : anchor;
        _rangeAnchor = anchor.Id;

        var pickable = visible.Where(CanMultiSelect).ToList();
        var at = pickable.IndexOf(cursor);
        if (at < 0) return null;
        var to = step switch
        {
            int.MinValue => 0,
            int.MaxValue => pickable.Count - 1,
            _ => Math.Clamp(at + step, 0, pickable.Count - 1),
        };
        var target = pickable[to];
        _rangeCursor = target.Id;

        int a = pickable.IndexOf(anchor), b = to;
        if (a > b) (a, b) = (b, a);
        _multi.Clear();
        // Anchor first so it stays the "start" in insertion order, then the rest top to bottom.
        _multi.Add(anchor.Id);
        foreach (var n in pickable.Skip(a).Take(b - a + 1))
            if (!_multi.Contains(n.Id)) _multi.Add(n.Id);
        ReapplyMultiSelection();
        return target;
    }

    /// <summary>Runs <paramref name="focus"/> (which selects a row in the tree) without resetting the Shift anchor.</summary>
    public void FocusWithinRange(EntryNode node, Action<EntryNode> focus)
    {
        _movingRangeFocus = true;
        try { focus(node); }
        finally { _movingRangeFocus = false; }
    }

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
        // Like Explorer, a later Shift range starts from the entry just Ctrl+clicked.
        _rangeAnchor = _rangeCursor = node.Id;
        ReapplyMultiSelection();
    }

    /// <summary>Shift+click: everything visible in the tree between the range anchor (normally the focused entry)
    /// and <paramref name="node"/>. The anchor stays, so another Shift+click or Shift+arrow re-draws from it.</summary>
    public void RangeSelect(EntryNode node, bool additive = false)
    {
        if (!CanMultiSelect(node)) return;
        var visible = VisibleNodes().ToList();
        if (RangeAnchor(visible) is not { } anchor) { node.IsSelected = true; return; }
        _rangeAnchor = anchor.Id;
        _rangeCursor = node.Id;
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

    /// <summary>Asks "Move X to Y?" before a move. Returns (move, don't ask again). Null moves without asking.</summary>
    public Func<string, string, (bool Move, bool DontAsk)>? AskMove { get; set; }

    /// <summary>Whether drag-drop and Move to ask first (More > Ask before moving entries).</summary>
    public bool ConfirmMoves
    {
        get => _settings.ConfirmMoves;
        set { _settings.ConfirmMoves = value; TrySaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>True when the move should go ahead: asking is off, nothing would move, or the user said Move.</summary>
    private bool ConfirmMove(IReadOnlyList<Guid> ids, Guid? folder)
    {
        if (!ConfirmMoves || AskMove is null || _store is null) return true;
        var moving = _store.MovableForMove(ids, folder);
        if (moving.Count == 0) return true; // nothing to ask about; the move itself does nothing
        var (title, text) = MovePrompt(moving, folder);
        var (move, dontAsk) = AskMove(title, text);
        if (move && dontAsk) ConfirmMoves = false;
        return move;
    }

    /// <summary>The question and details for moving <paramref name="moving"/> into <paramref name="folder"/> (null = top level).</summary>
    public (string Title, string Text) MovePrompt(IReadOnlyList<Entry> moving, Guid? folder)
    {
        var target = folder is { } f && _store?.Get(f) is { } fe ? $"\"{fe.Name}\"" : "the top level";
        string From(Entry e) => e.ParentId is { } p && _store?.Get(p) is { } pe ? FolderPath(pe) : "the top level";
        string Noun(Entry e) => e.Kind == EntryKind.Folder ? "folder " : "";
        var where = folder is { } f2 && _store?.Get(f2) is { } fe2 ? FolderPath(fe2) : "the top level";

        if (moving.Count == 1)
        {
            var e = moving[0];
            var text = $"From: {From(e)}\nTo: {where}";
            if (e.Kind == EntryKind.Folder) text += "\n\nEverything in the folder moves with it.";
            return ($"Move {Noun(e)}\"{e.Name}\" to {target}?", text);
        }

        var lines = moving.Take(8).Select(e => $"  •  {e.Name}{(e.Kind == EntryKind.Folder ? " (folder)" : "")}").ToList();
        if (moving.Count > 8) lines.Add($"  …and {moving.Count - 8} more");
        var body = $"To: {where}\n\n" + string.Join("\n", lines);
        if (moving.Any(e => e.Kind == EntryKind.Folder)) body += "\n\nFolders take everything in them along.";
        return ($"Move {moving.Count} entries to {target}?", body);
    }

    /// <summary>True when at least one of <paramref name="ids"/> can go into <paramref name="folder"/>.</summary>
    public bool CanMoveAny(IReadOnlyList<Guid> ids, Guid? folder) =>
        _store is not null && _store.MovableForMove(ids, folder).Count > 0;

    /// <summary>Moves several entries into <paramref name="folder"/> (null = top level). A selected folder takes its
    /// contents with it, so selected entries inside a selected folder stay where they are inside it.</summary>
    public async Task MoveManyAsync(IReadOnlyList<Guid> ids, Guid? folder)
    {
        if (_store is null) return;
        if (!ConfirmMove(ids, folder)) return;
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
