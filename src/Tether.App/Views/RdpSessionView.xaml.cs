using System.Windows;
using System.Windows.Controls;
using Tether.App.ViewModels;
using Tether.Protocols.Rdp;

namespace Tether.App.Views;

public partial class RdpSessionView : UserControl
{
    private RdpControl? _rdp;
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

        _rdp = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };
        Host.Child = _rdp;
        // Size the remote desktop to the tab before connecting.
        var dpi = VisualTreeHelperDpi();
        _rdp.Width = (int)(ActualWidth * dpi);
        _rdp.Height = (int)(ActualHeight * dpi);

        _rdp.Connected += () => vm.StatusText = $"RDP {vm.Endpoint}{(vm.UsedFallback ? " (fallback)" : "")}";
        _rdp.LoginComplete += () => vm.StatusText = $"RDP {vm.Endpoint}  {_rdp.Width}x{_rdp.Height}";
        _rdp.Disconnected += reason =>
        {
            vm.StatusText = $"RDP disconnected (code {reason})";
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Disconnected from {vm.Endpoint} (code {reason}).";
        };
        vm.Disposed += (_, _) => { _rdp?.Dispose(); _rdp = null; Host.Dispose(); };

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

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
}
