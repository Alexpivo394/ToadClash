using System.IO;

namespace ToadClash.DevAddin;

internal sealed record SyntheticTemplates(string Building, string Systems, string Generic, string Column)
{
    internal static SyntheticTemplates Find(SyntheticOptions options)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT 2022");
        var templates = new SyntheticTemplates(
            Path.Combine(root, "Templates", "English", "DefaultMetric.rte"),
            Path.Combine(root, "Templates", "English", "Systems-Default_Metric.rte"),
            Path.Combine(root, "Family Templates", "English", "Metric Generic Model.rft"),
            Path.Combine(root, "Family Templates", "English", "Metric Structural Column.rft"));
        foreach (var section in options.Sections)
        {
            var paths = section is "АР" or "КР"
                ? new[] { templates.Building, templates.Generic, templates.Column }
                : new[] { templates.Systems };
            foreach (var path in paths)
                if (!File.Exists(path)) throw new FileNotFoundException("Нет стандартного шаблона Revit 2022. Генерация остановлена.", path);
        }
        return templates;
    }
}
