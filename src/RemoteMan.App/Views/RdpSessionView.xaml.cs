using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using RemoteMan.App.ViewModels;
using RemoteMan.Protocols.Rdp;

namespace RemoteMan.App.Views;

/// <summary>
/// Embedded RDP. File transfer uses RDP's own clipboard channel, which carries files both ways:
/// copy files in Explorer on either side and paste on the other. Dropping files on the side strip
/// puts them on the clipboard and focuses the session, so one Ctrl+V in the remote folder finishes it.
/// </summary>
public partial class RdpSessionView : UserControl
{
    private RdpControl? _rdp;
    private CopyOnSelect? _copy;
    private RdpSessionViewModel? _vm;
    private bool _started;

    public RdpSessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not RdpSessionViewModel vm) return;
        _started = true;
        _vm = vm;

        _rdp = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };
        Host.Child = _rdp;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        _rdp.Width = (int)((ActualWidth - 22) * dpi);
        _rdp.Height = (int)(ActualHeight * dpi);

        _rdp.Connected += () => vm.StatusText = $"RDP {vm.Endpoint}{(vm.UsedFallback ? " (fallback)" : "")}";
        _rdp.LoginComplete += () => vm.StatusText = $"RDP {vm.Endpoint}  {_rdp.Width}x{_rdp.Height}";
        _rdp.Disconnected += reason =>
        {
            vm.StatusText = $"RDP disconnected (code {reason})";
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Disconnected from {vm.Endpoint} (code {reason}).";
        };
        vm.Disposed += (_, _) => { _copy?.Dispose(); _rdp?.Dispose(); _rdp = null; Host.Dispose(); };

        // Copy on select needs clipboard redirection to bring the text back.
        if (vm.Settings.CopyOnSelect && vm.Settings.RedirectClipboard) _copy = new CopyOnSelect(_rdp);

        try
        {
            vm.StatusText = $"RDP connecting to {vm.Endpoint}…";
            _rdp.Connect(vm.Endpoint, vm.Credential, vm.Settings);
        }
        catch (Exception ex)
        {
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Could not start RDP: {ex.Message}";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Strip.Background = (System.Windows.Media.Brush)FindResource("Select");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => Strip.Background = (System.Windows.Media.Brush)FindResource("Panel");

    private void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Send(files);
    }

    private void OnSendFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send files to the remote session" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) Send(dlg.FileNames);
    }

    private void Send(IEnumerable<string> paths)
    {
        if (_vm is null) return;
        if (!_vm.Settings.RedirectClipboard)
        {
            _vm.StatusText = "Turn on clipboard redirection for this entry to send files.";
            return;
        }
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        Clipboard.SetFileDropList(list);
        _rdp?.Focus();
        _vm.StatusText = $"{list.Count} file(s) ready: press Ctrl+V in a folder on the remote machine";
    }
}
