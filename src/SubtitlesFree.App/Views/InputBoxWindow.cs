using System.Windows;
using System.Windows.Controls;

namespace SubtitlesFree.App.Views;

/// <summary>单值输入小窗（WPF 无内置 InputBox）。确定=IsDefault 回车，取消=IsCancel/Esc。</summary>
public sealed class InputBoxWindow : Window
{
    private readonly TextBox _box = new();

    public string InputValue => _box.Text.Trim();

    public InputBoxWindow(string title, string prompt, string initial = "")
    {
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        _box.Text = initial;
        _box.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确定", Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 4, 18, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
