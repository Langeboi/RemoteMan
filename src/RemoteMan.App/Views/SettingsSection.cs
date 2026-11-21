using System.Windows;
using System.Windows.Controls;

namespace RemoteMan.App.Views;

/// <summary>
/// A closed-by-default section on the entry editor's Advanced tab: title, a one-line summary of what's set,
/// and (where the folder can supply it) a "Folder" tick that greys the section out and uses the folder's values.
/// </summary>
public sealed class SettingsSection : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsSection), new(""));
    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(nameof(Summary), typeof(string), typeof(SettingsSection), new(""));
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(SettingsSection),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CanInheritProperty = DependencyProperty.Register(nameof(CanInherit), typeof(bool), typeof(SettingsSection), new(false));
    public static readonly DependencyProperty IsInheritedProperty = DependencyProperty.Register(nameof(IsInherited), typeof(bool), typeof(SettingsSection),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Summary { get => (string)GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public bool CanInherit { get => (bool)GetValue(CanInheritProperty); set => SetValue(CanInheritProperty, value); }
    public bool IsInherited { get => (bool)GetValue(IsInheritedProperty); set => SetValue(IsInheritedProperty, value); }
}
