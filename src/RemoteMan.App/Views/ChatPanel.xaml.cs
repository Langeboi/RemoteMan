using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

public partial class ChatPanel : UserControl
{
    /// <summary>Drag format of entries dragged out of the tree (same as MainWindow's).</summary>
    public const string EntryDragFormat = "RemoteMan.EntryId";

    private ChatViewModel? _vm;
    private bool _settingCaret;

    /// <summary>Supplies the entry for a dropped id (the main view model's store).</summary>
    public Func<Guid, RemoteMan.Core.Model.Entry?>? ResolveEntry { get; set; }

    public ChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ChatViewModel);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(FocusBest, DispatcherPriority.Input);
            else if (_vm is not null) _vm.IsPickerOpen = false;
        };
        // The picker is a popup (it floats above other apps): close it when the message box loses focus to another window.
        Input.LostKeyboardFocus += (_, e) => { if (_vm is not null && e.NewFocus is null) _vm.IsPickerOpen = false; };
    }

    private void Attach(ChatViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.FocusComposer -= OnFocusComposer;
            _vm.ScrollToEnd -= OnScrollToEnd;
            _vm.People.CollectionChanged -= OnListsChanged;
            _vm.Messages.CollectionChanged -= OnListsChanged;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        vm.FocusComposer += OnFocusComposer;
        vm.ScrollToEnd += OnScrollToEnd;
        vm.People.CollectionChanged += OnListsChanged;
        vm.Messages.CollectionChanged += OnListsChanged;
        UpdateEmptyStates();
    }

    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyStates();

    private void UpdateEmptyStates()
    {
        if (_vm is null) return;
        NoPeople.Visibility = _vm.People.Count == 0 && _vm.Filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyRoom.Visibility = _vm.Current is not null && _vm.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.Visibility = _vm.Current is { IsGroup: true, IsEveryone: false } ? Visibility.Visible : Visibility.Collapsed;
        var group = _vm.Current?.IsGroup == true;
        hav.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        hgrp.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.Current)) UpdateEmptyStates();
        if (e.PropertyName is nameof(ChatViewModel.IsPickerOpen) && _vm is { IsPickerOpen: true }) Picker.HorizontalOffset = 0;
        if (e.PropertyName is nameof(ChatViewModel.PickSelected) && _vm?.PickSelected is { } sel) PickList.ScrollIntoView(sel);
    }

    private void OnFocusComposer(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_vm?.Current is null) { FilterBox.Focus(); return; }
        Input.Focus();
        _settingCaret = true;
        Input.CaretIndex = Math.Min(_vm.CaretAfterInsert > 0 ? _vm.CaretAfterInsert : Input.Text.Length, Input.Text.Length);
        _settingCaret = false;
    }, DispatcherPriority.Input);

    private void FocusBest()
    {
        if (_vm?.Current is null) FilterBox.Focus(); else Input.Focus();
    }

    private void OnScrollToEnd(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    // ---------------- composer ----------------

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsPickerOpen)
        {
            switch (e.Key)
            {
                case Key.Down: _vm.MovePick(1); e.Handled = true; return;
                case Key.Up: _vm.MovePick(-1); e.Handled = true; return;
                case Key.Enter or Key.Tab: _vm.Pick(null); e.Handled = true; return;
                case Key.Escape: _vm.IsPickerOpen = false; e.Handled = true; return;
            }
        }
        // Inside an open ``` block, Enter is a new line; closing the block makes Enter send again.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && InOpenFence()) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Input.Text.Length == 0)
        {
            _vm.BackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool InOpenFence()
    {
        var before = Input.Text[..Math.Min(Input.CaretIndex, Input.Text.Length)];
        var n = 0;
        foreach (var line in before.Split('\n'))
            if (line.TrimStart().StartsWith("```") && line.Split("```").Length == 2) n++;
        return n % 2 == 1;
    }

    /// <summary>
    /// The wheel scrolls the conversation, even over message text or a code block. Only an expanded
    /// code block that can still scroll that way keeps it (Shift+wheel scrolls code sideways).
    /// </summary>
    private void OnMessagesWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindCodeBox(e.OriginalSource as DependencyObject) is { } box)
        {
            var sv = FindChild<ScrollViewer>(box);
            if (Keyboard.Modifiers == ModifierKeys.Shift && sv is { ScrollableWidth: > 0 })
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
            if (box.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && sv is not null &&
                ((e.Delta < 0 && sv.VerticalOffset < sv.ScrollableHeight) || (e.Delta > 0 && sv.VerticalOffset > 0)))
                return;
        }
        MessagesScroll.ScrollToVerticalOffset(MessagesScroll.VerticalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private static TextBox? FindCodeBox(DependencyObject? d)
    {
        for (; d is not null && d is not ScrollViewer { Name: "MessagesScroll" }; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox { Tag: "chat-code" } t) return t;
        return null;
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e) => UpdateQuery();
    private void OnInputSelectionChanged(object sender, RoutedEventArgs e) { if (!_settingCaret) UpdateQuery(); }

    private void UpdateQuery()
    {
        if (_vm is null || !Input.IsKeyboardFocused) return;
        _vm.UpdateQuery(Input.Text, Input.CaretIndex);
    }

    private void OnPickClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ChatPickItem item) { _vm?.Pick(item); e.Handled = true; }
    }

    private static Guid? DraggedEntry(DragEventArgs e) =>
        e.Data.GetData(EntryDragFormat) is string s && Guid.TryParse(s, out var id) ? id : null;

    private void OnInputDragOver(object sender, DragEventArgs e)
    {
        if (DraggedEntry(e) is null) return;   // text drags work as usual
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        if (DraggedEntry(e) is not { } id || ResolveEntry?.Invoke(id) is not { } entry) return;
        _vm?.ShareEntry(entry);
        e.Handled = true;
    }
}
