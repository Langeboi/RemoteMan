using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>
/// The Overview tab. Cards sit two by two when there's room and both have something in them;
/// otherwise each takes the full width. The 2FA ring is drawn here from the seconds left.
/// </summary>
public partial class OverviewView : UserControl
{
    private const double TwoColumnWidth = 760;
    private OverviewViewModel? _vm;

    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as OverviewViewModel);
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange();
    }

    private void Attach(OverviewViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            foreach (var c in Lists(_vm)) c.CollectionChanged -= OnListChanged;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        foreach (var c in Lists(vm)) c.CollectionChanged += OnListChanged;
        Arrange();
        DrawRing();
    }

    private static IEnumerable<INotifyCollectionChanged> Lists(OverviewViewModel vm) => [vm.ConnectionRows, vm.LoginRows, vm.FieldRows];

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Arrange();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverviewViewModel.TotpLeft)) DrawRing();
        else if (e.PropertyName is nameof(OverviewViewModel.HasLogin) or nameof(OverviewViewModel.Description) or nameof(OverviewViewModel.HasTotp)) Arrange();
    }

    /// <summary>Two columns when wide and both cards show; one full-width column otherwise.</summary>
    private void Arrange()
    {
        if (_vm is not { } vm) return;
        var wide = ActualWidth >= TwoColumnWidth;
        Pair(TopGrid, ConnCard, vm.ConnectionRows.Count > 0, LoginCard, vm.HasLogin, wide);
        Pair(MoreGrid, FieldsCard, vm.FieldRows.Count > 0, DescCard, vm.Description.Length > 0, wide);
    }

    private static void Pair(Grid grid, FrameworkElement a, bool showA, FrameworkElement b, bool showB, bool wide)
    {
        grid.Visibility = showA || showB ? Visibility.Visible : Visibility.Collapsed;
        var side = wide && showA && showB;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(side ? 12 : 0);
        grid.ColumnDefinitions[2].Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Place(a, 0, 0, side);
        Place(b, side ? 0 : 1, side ? 2 : 0, side);
        b.Margin = new Thickness(0, !side && showA && showB ? 12 : 0, 0, 0);
    }

    private static void Place(FrameworkElement el, int row, int col, bool side)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        Grid.SetColumnSpan(el, side ? 1 : 3);
    }

    /// <summary>An arc from 12 o'clock, clockwise, for the part of the 30 s window still left.</summary>
    private void DrawRing()
    {
        if (_vm is not { } vm) return;
        var left = Math.Clamp(vm.TotpLeft, 0, 0.9999);
        TotpArc.Stroke = (Brush)FindResource(vm.TotpSeconds <= 5 ? "Warn" : "Accent");
        if (left <= 0) { TotpArc.Data = null; return; }
        const double r = 7.75, c = 9;
        var angle = left * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, left > 0.5, SweepDirection.Clockwise, true));
        TotpArc.Data = new PathGeometry([fig]);
    }
}
