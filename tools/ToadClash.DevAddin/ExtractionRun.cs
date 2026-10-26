using System.Diagnostics;
using System.Globalization;
using System.IO;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Revit;

namespace ToadClash.DevAddin;

/// <summary>
///     Выгрузка модели в файлы рядом с ней: <c>&lt;модель&gt;.tcg</c>, <c>&lt;модель&gt;.tcp</c> и строка итога в
///     <c>&lt;модель&gt;.toadclash.log</c>. Документ не изменяется.
/// </summary>
public static class ExtractionRun
{
    /// <summary>Выгружает документ по виду <paramref name="view"/> и возвращает итог: число элементов, треугольников, размеры, время.</summary>
    public static string Run(Document document, View3D view)
    {
        var modelName = Path.GetFileNameWithoutExtension(document.PathName);
        var basePath = Path.Combine(Path.GetDirectoryName(document.PathName)!, modelName);
        var visible = CountVisibleModelElements(document, view);

        var clock = Stopwatch.StartNew();
        var (geometry, parameters) = GeometryExtractor.Extract(document, new ExtractOptions(modelName, view.Name), CancellationToken.None);
        var extractSeconds = clock.Elapsed.TotalSeconds;

        using (var stream = File.Create(basePath + ".tcg")) ModelGeometryFile.Write(stream, geometry);
        using (var stream = File.Create(basePath + ".tcp")) ModelParametersFile.Write(stream, parameters);
        var totalSeconds = clock.Elapsed.TotalSeconds;

        bool complete;
        using (var stream = File.OpenRead(basePath + ".tcg")) complete = ModelGeometryFile.ReadHeader(stream).Complete;

        var summary = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss} {1}, вид \"{2}\": видимых модельных элементов {3}, выгружено с геометрией {4}, опись {5}, " +
            "треугольников {6}, .tcg {7:0.00} МБ, .tcp {8:0.00} МБ, выгрузка {9:0.0} с, с записью {10:0.0} с, Complete={11}",
            DateTime.Now,
            Path.GetFileName(document.PathName),
            view.Name,
            visible,
            geometry.Elements.Count,
            geometry.Inventory.Count,
            geometry.Elements.Sum(element => (long)element.Mesh.TriangleCount),
            new FileInfo(basePath + ".tcg").Length / 1048576.0,
            new FileInfo(basePath + ".tcp").Length / 1048576.0,
            extractSeconds,
            totalSeconds,
            complete);
        File.AppendAllText(basePath + ".toadclash.log", summary + Environment.NewLine);
        return summary;
    }

    /// <summary>Видимые на виде экземпляры модельных категорий, для сверки с числом выгруженных элементов.</summary>
    private static int CountVisibleModelElements(Document document, View3D view)
    {
        var count = 0;
        foreach (var element in document.CollectElements(view).WhereElementIsNotElementType())
        {
            if (element.Category is { CategoryType: CategoryType.Model }) count++;
        }

        return count;
    }
}
