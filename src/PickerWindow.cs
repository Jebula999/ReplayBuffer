using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReplayBuffer;

public sealed record PickerItem(string Label, object Value)
{
    public override string ToString() => Label;
}

/// <summary>Simple searchable "pick one" dialog.</summary>
public sealed class PickerWindow : Window
{
    readonly List<PickerItem> all;
    readonly ListBox list = new() { MinHeight = 300 };
    readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 10) };

    public PickerItem? Selected { get; private set; }

    public PickerWindow(string title, List<PickerItem> items)
    {
        all = items;
        if (TryFindResource(typeof(Window)) is Style themed) Style = themed;  // Fluent theme only styles the base type
        Title = title;
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var ok = new Button { Content = "Add", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => Accept();
        list.MouseDoubleClick += (_, _) => Accept();
        search.TextChanged += (_, _) => Filter();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(search);
        root.Children.Add(buttons);
        root.Children.Add(list);
        Content = root;

        Filter();
        Loaded += (_, _) => Keyboard.Focus(search);
    }

    void Filter()
    {
        string q = search.Text.Trim();
        list.ItemsSource = q.Length == 0 ? all : all.Where(i => i.Label.Contains(q, System.StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
    }

    void Accept()
    {
        Selected = list.SelectedItem as PickerItem;
        if (Selected != null) DialogResult = true;
    }
}
