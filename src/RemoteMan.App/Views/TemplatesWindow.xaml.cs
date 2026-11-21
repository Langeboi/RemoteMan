using System.Text;
using System.Windows;
using System.Windows.Controls;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

/// <summary>
/// Make, change and delete entry templates. A template's own settings (name, menu line, kind, folder, companion entry)
/// are edited here; what it fills in is edited in the normal entry editor (<see cref="EntryEditorWindow.TemplateMode"/>).
/// </summary>
public partial class TemplatesWindow : Window
{
    private sealed record KindOpt(EntryKind Kind, string Label) { public override string ToString() => Label; }
    private sealed record FolderOpt(Guid? Id, string Label) { public override string ToString() => Label; }

    private static readonly IReadOnlyList<KindOpt> Kinds = EntryMenu.EntryTypes
        .Where(t => t.Kind is not (EntryKind.Folder or EntryKind.Credential))
        .Select(t => new KindOpt(t.Kind, t.Name)).ToList();
    private static readonly IReadOnlyList<KindOpt> CompanionKinds =
        [new(EntryKind.Ssh, "SSH"), new(EntryKind.Web, "Web page"), new(EntryKind.Rdp, "RDP"), new(EntryKind.Telnet, "Telnet"), new(EntryKind.Sftp, "SFTP / SCP")];

    private readonly MainViewModel _vm;
    /// <summary>The template being edited: a copy of a stored one, or a new one not saved yet.</summary>
    private Entry? _work;
    private bool _dirty;
    private bool _loading;

    public TemplatesWindow(MainViewModel vm, Entry? start = null)
    {
        InitializeComponent();
        _vm = vm;
        KindBox.ItemsSource = Kinds;
        CompanionKindBox.ItemsSource = CompanionKinds;
        TemplateList.ItemsSource = vm.Templates;
        vm.TemplatesChanged += OnTemplatesChanged;
        Closed += (_, _) => vm.TemplatesChanged -= OnTemplatesChanged;
        RefreshFolders();
        UpdateEmpty();
        if (start is not null) Open(start, isNew: true);
        else if (vm.Templates.Count > 0) TemplateList.SelectedIndex = 0;
        else Open(null, false);
    }

    private void RefreshFolders()
    {
        var keep = (FolderBox.SelectedItem as FolderOpt)?.Id;
        var opts = new List<FolderOpt> { new(null, "The folder selected when you use it") };
        opts.AddRange(_vm.FolderChoices().Select(f => new FolderOpt(f.Id, f.Path)));
        FolderBox.ItemsSource = opts;
        FolderBox.SelectedItem = opts.FirstOrDefault(o => o.Id == keep) ?? opts[0];
    }

    private void UpdateEmpty()
    {
        EmptyHint.Visibility = _vm.Templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartersButton.IsEnabled = EntryTemplates.Starters().Any(s => _vm.Templates.All(t => !t.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Someone (maybe you) saved or deleted a template: keep the one being edited selected.</summary>
    private void OnTemplatesChanged(object? sender, EventArgs e)
    {
        UpdateEmpty();
        _loading = true;
        try { TemplateList.SelectedItem = _work is null ? null : _vm.Templates.FirstOrDefault(t => t.Id == _work.Id); }
        finally { _loading = false; }
    }

    private void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TemplateList.SelectedItem is not Entry t || t.Id == _work?.Id) return;
        if (!ConfirmLeave()) { _loading = true; TemplateList.SelectedItem = _work is null ? null : _vm.Templates.FirstOrDefault(x => x.Id == _work.Id); _loading = false; return; }
        Open(EntryStore.Clone(t), isNew: false);
    }

    /// <summary>Unsaved changes: save them, drop them, or stay. False = stay.</summary>
    private bool ConfirmLeave()
    {
        if (!_dirty || _work is null) return true;
        var choice = ChoiceDialog.Show(this, $"Save changes to \"{NameBox.Text.Trim()}\"?", "", ["Save", "Don't save", "Cancel"]);
        if (choice == 0)
        {
            ReadForm();
            if (_work.Name.Length == 0) { NameBox.Focus(); return false; }
            _ = _vm.SaveTemplateAsync(_work);
            _dirty = false;
            return true;
        }
        if (choice == 1) _dirty = false;
        return choice == 1;
    }

    private void Open(Entry? template, bool isNew)
    {
        _loading = true;
        try
        {
            _work = template;
            Details.IsEnabled = template is not null;
            DuplicateButton.IsEnabled = template is not null;
            if (template is null)
            {
                DetailHeading.Text = "No template selected";
                NameBox.Text = SummaryBox.Text = ""; FillsIn.Text = "";
                return;
            }
            var data = template.Template ??= new TemplateData();
            DetailHeading.Text = isNew || template.Revision == 0 ? "New template (not saved yet)" : template.Name;
            NameBox.Text = template.Name;
            SummaryBox.Text = data.Summary ?? "";
            KindBox.SelectedItem = Kinds.FirstOrDefault(k => k.Kind == data.Kind) ?? Kinds[0];
            RefreshFolders();
            FolderBox.SelectedItem = FolderBox.Items.OfType<FolderOpt>().FirstOrDefault(o => o.Id == data.FolderId) ?? FolderBox.Items[0];
            CompanionBox.IsChecked = data.CompanionKind is not null;
            CompanionKindBox.SelectedItem = CompanionKinds.FirstOrDefault(k => k.Kind == data.CompanionKind) ?? CompanionKinds[0];
            CompanionPortBox.Text = data.CompanionPort > 0 ? data.CompanionPort.ToString() : "";
            CompanionSuffixBox.Text = data.CompanionSuffix ?? "";
            CompanionRow.IsEnabled = CompanionBox.IsChecked == true;
            DeleteButton.IsEnabled = template.Revision > 0;
            _dirty = isNew || template.Revision == 0;
            UpdateFillsIn();
            UpdateButtons();
        }
        finally { _loading = false; }
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (CompanionRow is not null) CompanionRow.IsEnabled = CompanionBox?.IsChecked == true;
        if (_loading || _work is null) return;
        if (ReferenceEquals(sender, CompanionKindBox) && CompanionKindBox.SelectedItem is KindOpt k && CompanionPortBox.Text.Trim().Length == 0)
            CompanionPortBox.Text = Entry.DefaultPort(k.Kind).ToString();
        ReadForm();
        _dirty = true;
        UpdateFillsIn();
        UpdateButtons();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _work?.Template is not { } data || KindBox.SelectedItem is not KindOpt k || k.Kind == data.Kind) return;
        var proto = EntryTemplates.Prototype(_work);
        var hasSettings = proto.Tags is not null || proto.Fields is { Count: > 0 } || proto.Icon is not null || proto.Login != LoginSource.None;
        if (hasSettings && !ChoiceDialog.Confirm(this, $"Make {k.Label} entries instead?", "Protocol settings start over. The login, icon, tags and custom fields are kept.", "Change"))
        {
            _loading = true; KindBox.SelectedItem = Kinds.FirstOrDefault(x => x.Kind == data.Kind); _loading = false;
            return;
        }
        var fresh = EntryTemplates.Blank(k.Kind);
        fresh.Login = proto.Login; fresh.CredentialId = proto.CredentialId; fresh.Credential = proto.Credential;
        fresh.Icon = proto.Icon; fresh.IconColor = proto.IconColor; fresh.Tags = proto.Tags; fresh.Fields = proto.Fields;
        fresh.Description = proto.Description; fresh.Connection = proto.Connection;
        EntryTemplates.SetPrototype(_work, fresh);
        _dirty = true;
        UpdateFillsIn();
        UpdateButtons();
    }

    private void ReadForm()
    {
        if (_work?.Template is not { } data) return;
        _work.Name = NameBox.Text.Trim();
        data.Summary = SummaryBox.Text.Trim() is { Length: > 0 } s ? s : null;
        data.FolderId = (FolderBox.SelectedItem as FolderOpt)?.Id;
        data.CompanionKind = CompanionBox.IsChecked == true ? (CompanionKindBox.SelectedItem as KindOpt)?.Kind ?? EntryKind.Ssh : null;
        data.CompanionPort = int.TryParse(CompanionPortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 0;
        data.CompanionSuffix = CompanionSuffixBox.Text.Length > 0 ? CompanionSuffixBox.Text : null;
    }

    private void UpdateButtons()
    {
        SaveButton.IsEnabled = _work is not null && _dirty && NameBox.Text.Trim().Length > 0;
        UseButton.IsEnabled = _work is not null && NameBox.Text.Trim().Length > 0;
        Status.Text = _dirty ? "Unsaved changes" : "";
    }

    /// <summary>A short list of what new entries get, so you can check a template without opening the editor.</summary>
    private void UpdateFillsIn()
    {
        if (_work is null) { FillsIn.Text = ""; return; }
        var p = EntryTemplates.Prototype(_work);
        var sb = new StringBuilder();
        void Line(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"{k}:  {v}"); }
        Line("Kind", Kinds.FirstOrDefault(k => k.Kind == p.Kind)?.Label);
        if (p.Kind == EntryKind.Web) Line("Address", p.Web?.Url);
        var ports = p.Endpoints.Where(h => h.Port > 0).Select(h => (h.Host.Length > 0 ? h.Host : "host") + ":" + h.Port).ToList();
        if (ports.Count > 0) Line(p.Kind == EntryKind.Web ? "Fallback" : "Host", string.Join(", then ", ports));
        Line("Login", p.Login switch
        {
            LoginSource.Saved => "Linked credential: " + (p.CredentialId is { } cid ? _vm.Store?.Get(cid)?.Name ?? "(deleted)" : "(none)"),
            LoginSource.Inherited => "From the folder",
            LoginSource.WindowsLogin => "My Windows login",
            LoginSource.Embedded => "Typed in: " + (p.Credential?.UserName ?? ""),
            _ => "None (asks when connecting)",
        });
        if (p.Icon is { Length: > 0 } icon) Line("Icon", VendorMarks.Find(icon)?.Name ?? icon);
        Line("Tags", p.Tags);
        if (p.Fields is { Count: > 0 } f) Line("Custom fields", string.Join(", ", f.Select(x => x.Name + (string.IsNullOrEmpty(x.Value) ? "" : x.Secret ? " (set)" : $" = {x.Value}"))));
        if (p.Ssh is { } ssh && p.Kind is EntryKind.Ssh or EntryKind.Telnet)
        {
            Line("SSH", ssh.AllowLegacyAlgorithms ? "Older algorithms allowed" : "Modern algorithms only");
            if (ssh.JumpHostId is { } j) Line("Jump host", _vm.Store?.Get(j)?.Name);
        }
        if (p.Rdp is { } rdp && p.Kind == EntryKind.Rdp)
            Line("RDP", string.Join(", ", new[] { rdp.AdminSession ? "admin session" : null, rdp.SmartSizing ? "fit to tab" : null, rdp.RedirectClipboard ? "clipboard" : null }.Where(x => x is not null)));
        if (p.Web is { } web && p.Kind == EntryKind.Web)
            Line("Browser", (web.IgnoreCertificateErrors ? "skips certificate errors" : "checks certificates") + (web.AutoFill ? ", fills the login form" : ""));
        if (p.Description is { Length: > 0 } d) Line("Description", d.Split('\n')[0]);
        FillsIn.Text = sb.ToString().TrimEnd();
    }

    private void OnEditSettings(object sender, RoutedEventArgs e)
    {
        if (_work is null || _vm.Store is not { } store || _vm.Protector is not { } protector) return;
        ReadForm();
        var proto = EntryTemplates.Prototype(_work);
        proto.Name = _work.Name;
        proto.ParentId = _work.Template?.FolderId;
        var kindLabel = Kinds.FirstOrDefault(k => k.Kind == proto.Kind)?.Label ?? proto.Kind.ToString();
        var dlg = new EntryEditorWindow(proto, store, protector)
        {
            Owner = this, TemplateMode = true,
            TemplateHeading = $"Template \"{(_work.Name.Length > 0 ? _work.Name : "new")}\": what new {kindLabel} entries start with",
        };
        if (dlg.ShowDialog() != true) return;
        proto.ParentId = null;
        EntryTemplates.SetPrototype(_work, proto);
        _dirty = true;
        UpdateFillsIn();
        UpdateButtons();
    }

    private async Task<Entry?> SaveAsync()
    {
        if (_work is null) return null;
        ReadForm();
        if (_work.Name.Length == 0) { NameBox.Focus(); return null; }
        var saved = await _vm.SaveTemplateAsync(_work);
        if (saved is null) { Status.Text = _vm.Message; return null; }
        Open(EntryStore.Clone(saved), isNew: false);
        _loading = true;
        TemplateList.SelectedItem = _vm.Templates.FirstOrDefault(t => t.Id == saved.Id);
        _loading = false;
        Status.Text = $"Saved {saved.Name}";
        return saved;
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void OnUse(object sender, RoutedEventArgs e)
    {
        var t = _dirty ? await SaveAsync() : _vm.Templates.FirstOrDefault(x => x.Id == _work?.Id);
        if (t is null) return;
        Close();
        await _vm.NewFromTemplateAsync(t, _vm.NewEntryParent());
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_work is null || _work.Revision == 0) return;
        if (!ChoiceDialog.Confirm(this, $"Delete template \"{_work.Name}\"?", "It is removed for everyone on this data source. Entries made from it stay as they are.", "Delete", danger: true)) return;
        var gone = _work;
        _dirty = false;
        Open(null, false);
        await _vm.DeleteTemplateAsync(gone);
        if (_vm.Templates.Count > 0) TemplateList.SelectedIndex = 0;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        _loading = true; TemplateList.SelectedItem = null; _loading = false;
        Open(EntryTemplates.Create("", EntryKind.Ssh), isNew: true);
        NameBox.Focus();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_work is null || !ConfirmLeave()) return;
        var copy = EntryStore.Clone(_work);
        ReadFormInto(copy);
        copy.Id = Guid.NewGuid();
        copy.Revision = 0;
        copy.CreatedAt = null; copy.CreatedBy = null; copy.UpdatedBy = null;
        copy.Name = (copy.Name.Length > 0 ? copy.Name : "Template") + " (copy)";
        _loading = true; TemplateList.SelectedItem = null; _loading = false;
        Open(copy, isNew: true);
    }

    /// <summary>A duplicate starts from what is on screen, saved or not.</summary>
    private void ReadFormInto(Entry copy)
    {
        var keep = _work;
        _work = copy;
        ReadForm();
        _work = keep;
    }

    private async void OnStarters(object sender, RoutedEventArgs e)
    {
        var n = await _vm.AddStarterTemplatesAsync();
        Status.Text = n == 0 ? _vm.Message : $"Added {n} starter template{(n == 1 ? "" : "s")}. Change them to match your setup.";
        if (_work is null && _vm.Templates.Count > 0) TemplateList.SelectedIndex = 0;
        UpdateEmpty();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmLeave()) e.Cancel = true;
        base.OnClosing(e);
    }
}
