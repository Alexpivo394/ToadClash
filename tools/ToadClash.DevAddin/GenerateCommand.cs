using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ToadClash.DevAddin;

/// <summary>Создаёт новый набор синтетических моделей в отдельном каталоге.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class GenerateCommand : ExternalCommand
{
    public override void Execute()
    {
        var section = new ComboBox { ItemsSource = new[] { "Все", "АР", "КР", "ВК", "ОВ", "ЭОМ" }, SelectedIndex = 0 };
        var floors = new TextBox { Text = "20" };
        var cells = new TextBox { Text = "500" };
        var extract = new CheckBox { Content = "Выгрузить геометрию после создания", IsChecked = true };
        var message = new TextBlock { Text = "Все разделы × 20 этажей × 500 ячеек: минимум 150 000 элементов.\nДля пробного запуска задайте 1 этаж и 2 ячейки.", TextWrapping = TextWrapping.Wrap };
        var button = new Button { Content = "Создать новые модели", IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var pair in new[] { ("Раздел", (UIElement)section), ("Этажей (1–100)", floors), ("Ячеек на этаже (1–500)", cells) })
        {
            panel.Children.Add(new TextBlock { Text = pair.Item1, Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(pair.Item2);
        }
        panel.Children.Add(extract);
        panel.Children.Add(message);
        panel.Children.Add(button);
        var window = new Window { Title = "ToadClash — синтетические модели", Width = 460, SizeToContent = SizeToContent.Height,
            Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        SyntheticOptions? options = null;
        button.Click += (_, _) =>
        {
            if (!int.TryParse(floors.Text, out var floorCount) || !int.TryParse(cells.Text, out var cellCount))
            {
                message.Text = "Введите целое число этажей и ячеек.";
                return;
            }
            try { options = new SyntheticOptions((string)section.SelectedItem, floorCount, cellCount); }
            catch (ArgumentException exception) { message.Text = exception.Message; return; }
            window.DialogResult = true;
        };
        if (window.ShowDialog() != true || options is null) { Result = Result.Cancelled; return; }
        try
        {
            var directory = SyntheticGenerationRun.Run(Application.Application, options, extract.IsChecked == true);
            TaskDialog.Show("ToadClash", "Модели созданы. Каталог запуска:\n" + directory);
        }
        catch (Exception exception)
        {
            TaskDialog.Show("ToadClash", exception.Message);
            Result = Result.Failed;
        }
    }
}
