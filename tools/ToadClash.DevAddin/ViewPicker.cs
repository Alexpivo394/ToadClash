using System.Windows;
using System.Windows.Controls;

namespace ToadClash.DevAddin;

/// <summary>Окно выбора 3D-вида из списка.</summary>
internal static class ViewPicker
{
    /// <summary>Выбранный вид или null, если окно закрыто без подтверждения.</summary>
    public static View3D? Pick(IReadOnlyList<View3D> views, View3D preselected)
    {
        var list = new ComboBox
        {
            ItemsSource = views,
            DisplayMemberPath = nameof(View3D.Name),
            SelectedItem = preselected,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var ok = new Button { Content = "Выгрузить", IsDefault = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "3D-вид для выгрузки:", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(list);
        panel.Children.Add(ok);

        var window = new Window
        {
            Title = "ToadClash",
            Content = panel,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        ok.Click += (_, _) => window.DialogResult = true;

        return window.ShowDialog() == true ? list.SelectedItem as View3D : null;
    }
}
