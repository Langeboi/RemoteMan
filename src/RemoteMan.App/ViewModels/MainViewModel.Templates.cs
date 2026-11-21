using System.Collections.ObjectModel;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>Entry templates: listed in the New menu, kept in the data source so everyone on it shares them.</summary>
public partial class MainViewModel
{
    /// <summary>All templates, by name. The New menu and the Templates window show these.</summary>
    public ObservableCollection<Entry> Templates { get; } = new();
    public event EventHandler? TemplatesChanged;

    private void RefreshTemplates()
    {
        if (_store is null) return;
        var list = _store.Entries.Where(e => e.Kind == EntryKind.Template && !e.Deleted)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Templates.Clear();
        foreach (var t in list) Templates.Add(t);
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the editor on a new entry made from <paramref name="template"/>; it goes in the template's folder, else <paramref name="parent"/>.</summary>
    public Task NewFromTemplateAsync(Entry template, Guid? parent)
    {
        if (parent == FavoritesId || parent == DataSourceRootId) parent = null;
        var e = EntryTemplates.NewEntry(template, parent, id => _store?.Get(id) is { Kind: EntryKind.Folder, Deleted: false });
        return EditAndSaveAsync(e, template);
    }

    /// <summary>The folder the New menu creates in: the selected folder, or the selected entry's folder.</summary>
    public Guid? NewEntryParent()
    {
        var p = Selected?.Entry is { } s ? (s.Kind == EntryKind.Folder ? s.Id : s.ParentId) : null;
        return p == FavoritesId ? null : p;
    }

    public async Task<Entry?> SaveTemplateAsync(Entry template)
    {
        if (_store is null) return null;
        try { return await _store.SaveAsync(template); }
        catch (Exception ex) { Message = ex is DataSourceOfflineException ? "Offline: templates can't be changed until the database is back" : ex.Message; return null; }
    }

    public async Task DeleteTemplateAsync(Entry template)
    {
        if (_store is null) return;
        try { await _store.DeleteAsync(template.Id); }
        catch (Exception ex) { Message = ex.Message; }
    }

    /// <summary>Adds the starter templates that aren't there yet (matched by name). Returns how many were added.</summary>
    public async Task<int> AddStarterTemplatesAsync()
    {
        if (_store is null || !_store.IsOnline) return 0;
        var have = Templates.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
        var added = 0;
        foreach (var t in EntryTemplates.Starters())
        {
            if (have.Contains(t.Name.ToLowerInvariant())) continue;
            if (await SaveTemplateAsync(t) is null) break;
            added++;
        }
        return added;
    }

    /// <summary>Folders by path, for the template's folder picker.</summary>
    public IReadOnlyList<(Guid Id, string Path)> FolderChoices() =>
        Folders.Where(f => !f.Deleted).Select(f => (Id: f.Id, Path: FolderPath(f))).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Right-click "Save as template…": opens the Templates window on a new template made from the entry (set by the window).</summary>
    public Action<Entry>? SaveAsTemplateDialog { get; set; }

    /// <summary>Opens the Templates window (set by the window).</summary>
    public Action? TemplatesDialog { get; set; }
}
