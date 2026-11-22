using System.Windows;
using System.Windows.Controls;

namespace RemoteMan.App.Views;

/// <summary>Shows itself only while its session is shared (DataContext is the session's ShareBarViewModel, or null).</summary>
public partial class ShareBar : UserControl
{
    public ShareBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Visibility = DataContext is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
