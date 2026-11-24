using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RemoteMan.App.Services;
using RemoteMan.Core.Chat;

namespace RemoteMan.App.Views;

/// <summary>More > Chat GIFs (GIPHY): the API key and content rating everyone on the data source uses.</summary>
public sealed class GiphySettingsWindow : Window
{
    private readonly IntegrationSettings _shared;
    private readonly PasswordBox _key = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _rating = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock _result = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button _save = new() { Content = "Save", MinWidth = 76, IsDefault = true };
    private bool _remove;

    private static readonly (string Value, string Name)[] Ratings =
    [
        ("g", "G: suitable for everyone"),
        ("pg", "PG"),
        ("pg-13", "PG-13"),
        ("r", "R: no limit"),
    ];

    public GiphySettingsWindow(Window owner, IntegrationSettings shared)
    {
        _shared = shared;
        Owner = owner;
        Title = "Chat GIFs (GIPHY)";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "RemoteManWindow");

        var root = new StackPanel { Margin = new Thickness(16) };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 12),
            Text = (shared.WhyNotChangeable ?? shared.Note(shared.GiphyRow is null ? IntegrationOrigin.None : IntegrationOrigin.DataSource, shared.GiphyRow))
                   + (shared.GiphyProblem is { } p ? " " + p : "") };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(note);

        root.Children.Add(new TextBlock { Text = "GIPHY API key", FontWeight = FontWeights.SemiBold });
        root.Children.Add(_key);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 12) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        if (shared.GiphyApiKey is not null) hint.Inlines.Add("A key is saved. Leave this empty to keep it, or paste a new one. ");
        hint.Inlines.Add("Make a free one at ");
        var link = new Hyperlink(new Run("developers.giphy.com")) { NavigateUri = new Uri("https://developers.giphy.com/dashboard/") };
        link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { } };
        hint.Inlines.Add(link);
        hint.Inlines.Add(" (Create an app, choose API). It's saved in the data source, encrypted with its vault passphrase, and the GIF button shows in everyone's chat. " +
                         "Searches go from each PC to GIPHY; the GIF you pick is sent as a file, so the people reading it don't contact GIPHY.");
        root.Children.Add(hint);

        root.Children.Add(new TextBlock { Text = "Content rating", FontWeight = FontWeights.SemiBold });
        foreach (var (v, n) in Ratings) _rating.Items.Add(new ComboBoxItem { Content = n, Tag = v });
        _rating.SelectedIndex = Math.Max(0, Array.FindIndex(Ratings, r => r.Value == shared.GiphyRating));
        root.Children.Add(_rating);

        var test = new DockPanel();
        var testButton = new Button { Content = "Test key", MinWidth = 90 };
        testButton.Click += async (_, _) => await TestAsync();
        test.Children.Add(testButton);
        _result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        test.Children.Add(_result);
        root.Children.Add(test);

        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        if (shared.GiphyApiKey is not null)
        {
            var remove = new Button { Content = "Remove key" };
            remove.Click += async (_, _) => { _remove = true; await SaveAsync(); };
            remove.IsEnabled = shared.CanChange;
            buttons.Children.Add(remove);
        }
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_save, Dock.Right);
        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        _save.IsEnabled = shared.CanChange;
        _save.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => _key.Focus();
    }

    private string Rating => (_rating.SelectedItem as ComboBoxItem)?.Tag as string ?? "g";

    private async Task TestAsync()
    {
        var key = _key.Password.Trim().Length > 0 ? _key.Password.Trim() : _shared.GiphyApiKey;
        if (string.IsNullOrEmpty(key)) { _result.Text = "Paste a key first"; return; }
        _result.Text = "Testing…";
        try
        {
            var gifs = await new GiphyClient(key, Rating).SearchAsync("hello");
            _result.Text = $"OK: GIPHY answered with {gifs.Count} GIFs";
        }
        catch (Exception ex) { _result.Text = ex.Message; }
    }

    private async Task SaveAsync()
    {
        _save.IsEnabled = false;
        try
        {
            await _shared.SaveGiphyAsync(_remove ? "" : _key.Password.Trim().Length > 0 ? _key.Password.Trim() : null, Rating);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _result.Text = $"Couldn't save: {ex.Message}";
            _save.IsEnabled = true;
            _remove = false;
        }
    }
}
