using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RemoteMan.App.Views;

/// <summary>
/// Calmer, smooth mouse-wheel scrolling for long lists (entry tree, search results, snippets...).
/// WPF's own wheel handling scrolls three whole items per wheel event, whatever the event's size, so a precision
/// touchpad or a free-spinning wheel (many small events) races through the list. This scrolls by pixels in
/// proportion to the wheel delta: one notch = Windows' "lines to scroll" setting x <see cref="PixelsPerLine"/>,
/// eased over a few frames. Turning it on also switches the list to pixel scrolling, which keeps virtualization.
/// Usage: views:SmoothScroll.IsEnabled="True" on a TreeView, ListBox or ScrollViewer.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Pixels per wheel "line". With Windows' default of 3 lines a notch moves 42 px, about two tree rows.</summary>
    public const double PixelsPerLine = 14;

    /// <summary>Time constant of the ease, in seconds: the scroll covers ~95% of a notch within 3x this.</summary>
    private const double Tau = 0.045;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScroll), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
            fe.PreviewMouseWheel += OnPreviewMouseWheel;
            fe.Unloaded += OnUnloaded;
        }
        else
        {
            fe.PreviewMouseWheel -= OnPreviewMouseWheel;
            fe.Unloaded -= OnUnloaded;
            (fe.GetValue(StateProperty) as State)?.Stop();
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) =>
        ((sender as DependencyObject)?.GetValue(StateProperty) as State)?.Stop();

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement host || e.Delta == 0) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;   // Ctrl+wheel (zoom) and Shift+wheel keep their usual meaning

        var state = host.GetValue(StateProperty) as State;
        if (state?.Viewer is null || !state.Viewer.IsDescendantOf(host) && !ReferenceEquals(state.Viewer, host))
        {
            var viewer = host as ScrollViewer ?? FindScrollViewer(host);
            if (viewer is null) return;
            state = new State(viewer);
            host.SetValue(StateProperty, state);
        }

        var sv = state.Viewer;
        // A scrollable area inside a row (a text box, a nested list) scrolls itself first.
        if (e.OriginalSource is DependencyObject src && HasInnerScroller(src, sv, e.Delta)) return;
        if (sv.ScrollableHeight <= 0) return;   // nothing to scroll: let the parent have the wheel

        double lines = SystemParameters.WheelScrollLines;
        double step = lines < 0 ? Math.Max(sv.ViewportHeight - PixelsPerLine, PixelsPerLine) : lines * PixelsPerLine;
        state.ScrollBy(-e.Delta / 120.0 * step);
        e.Handled = true;
    }

    private static bool HasInnerScroller(DependencyObject from, ScrollViewer outer, int delta)
    {
        for (var d = from; d is not null && !ReferenceEquals(d, outer); d = Parent(d))
        {
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                bool canMove = delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight;
                if (canMove) return true;
            }
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is ScrollViewer sv && !ReferenceEquals(d, root)) return sv;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    private sealed class State(ScrollViewer viewer)
    {
        public ScrollViewer Viewer { get; } = viewer;
        private double _target, _lastSet;
        private bool _running;
        private readonly Stopwatch _clock = new();

        public void ScrollBy(double pixels)
        {
            // Keep stacking notches onto the running target; otherwise start from where the list is now.
            if (!_running) _target = Viewer.VerticalOffset;
            _target = Math.Clamp(_target + pixels, 0, Viewer.ScrollableHeight);
            if (_running) return;
            _running = true;
            _lastSet = Viewer.VerticalOffset;
            _clock.Restart();
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            double now = Viewer.VerticalOffset;
            // Someone else moved the list (scrollbar drag, keyboard, BringIntoView): give way to them.
            if (Math.Abs(now - _lastSet) > 1.5 || !Viewer.IsLoaded) { Stop(); return; }

            double dt = Math.Min(_clock.Elapsed.TotalSeconds, 0.1);
            _clock.Restart();
            _target = Math.Clamp(_target, 0, Viewer.ScrollableHeight);
            double remaining = _target - now;
            double next = Math.Abs(remaining) < 0.5 ? _target : now + remaining * (1 - Math.Exp(-dt / Tau));
            Viewer.ScrollToVerticalOffset(next);
            _lastSet = next;
            if (next == _target) Stop();
        }
    }
}
