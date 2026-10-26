using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;

namespace ToadClash.DevAddin;

/// <summary>Выгружает активную модель: выбор 3D-вида, запись .tcg/.tcp рядом с моделью, итог в журнал.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ExtractCommand : ExternalCommand
{
    public override void Execute()
    {
        var document = Application.ActiveUIDocument.Document;
        if (string.IsNullOrEmpty(document.PathName))
        {
            TaskDialog.Show("ToadClash", "Модель не сохранена: выгрузке нужен путь к файлу.");
            Result = Result.Cancelled;
            return;
        }

        var views = document.CollectElements().OfClass(typeof(View3D)).Cast<View3D>()
            .Where(view => !view.IsTemplate)
            .OrderBy(view => view.Name, StringComparer.CurrentCulture)
            .ToList();
        if (views.Count == 0)
        {
            TaskDialog.Show("ToadClash", "В модели нет 3D-видов.");
            Result = Result.Cancelled;
            return;
        }

        var preselected = document.ActiveView as View3D ?? views.FirstOrDefault(view => view.Name == "{3D}") ?? views[0];
        if (ViewPicker.Pick(views, preselected) is not { } chosen)
        {
            Result = Result.Cancelled;
            return;
        }

        TaskDialog.Show("ToadClash", ExtractionRun.Run(document, chosen));
    }
}
