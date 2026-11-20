using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RemoteMan.App.Views;

/// <summary>
/// The eye button inside every password box: passwords show as dots, and the eye shows the text
/// while it is pressed in. Editing the shown text writes back to the password box, so the windows
/// that read <see cref="PasswordBox.Password"/> don't need to know. Hiding again happens when the
/// eye is pressed again or the box loses focus. The template parts live in Theme/Dark.xaml.
/// </summary>
public static class PasswordReveal
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PasswordReveal), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(PasswordReveal), new PropertyMetadata(false));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb || e.NewValue is not true) return;
        if (pb.IsLoaded) Hook(pb); else pb.Loaded += (_, _) => Hook(pb);
    }

    private static void Hook(PasswordBox pb)
    {
        if ((bool)pb.GetValue(HookedProperty)) return;
        pb.ApplyTemplate();
        if (pb.Template?.FindName("PART_Eye", pb) is not ToggleButton eye
            || pb.Template.FindName("PART_Reveal", pb) is not TextBox reveal
            || pb.Template.FindName("PART_ContentHost", pb) is not FrameworkElement host) return;
        pb.SetValue(HookedProperty, true);

        var syncing = false;
        void UpdateEye() => eye.Visibility = pb.Password.Length > 0 || eye.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pb.PasswordChanged += (_, _) =>
        {
            if (!syncing && eye.IsChecked == true) { syncing = true; reveal.Text = pb.Password; syncing = false; }
            UpdateEye();
        };
        reveal.TextChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            pb.Password = reveal.Text;
            syncing = false;
        };
        eye.Checked += (_, _) =>
        {
            syncing = true; reveal.Text = pb.Password; syncing = false;
            reveal.Visibility = Visibility.Visible;
            host.Visibility = Visibility.Hidden;
            eye.ToolTip = "Hide password";
            reveal.Focus();
            reveal.CaretIndex = reveal.Text.Length;
        };
        eye.Unchecked += (_, _) =>
        {
            reveal.Visibility = Visibility.Collapsed;
            host.Visibility = Visibility.Visible;
            eye.ToolTip = "Show password";
            syncing = true; reveal.Text = ""; syncing = false;   // don't leave the text in a TextBox
            UpdateEye();
        };
        // Hide again when focus leaves the box for something else in the window.
        reveal.LostKeyboardFocus += (_, a) =>
        {
            if (a.NewFocus is DependencyObject next && (next == pb || next == eye)) return;
            eye.IsChecked = false;
        };
        pb.Unloaded += (_, _) => eye.IsChecked = false;
        UpdateEye();
    }
}
