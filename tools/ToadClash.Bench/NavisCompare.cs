using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadClash.Bench;

/// <summary>Каноническая пара элементов разового сравнения.</summary>
public sealed record ComparisonPair(ElementRef A, ElementRef B);

/// <summary>Различия текущих пересечений. Unmapped не участвуют в статистике пропусков движка.</summary>
public sealed record NavisComparisonReport(string Test, IReadOnlyList<ComparisonPair> Common,
    IReadOnlyList<ComparisonPair> OnlyNavis, IReadOnlyList<ClashHit> OnlyOurs,
    IReadOnlyList<string> Unmapped, int IgnoredResolved);

/// <summary>Одноразовая проверка этапа 0. Не импортирует наборы, статусы или коллизии в продукт.</summary>
public static class NavisCompare
{
    /// <summary>Сравнивает один XML-тест с прогоном явно заданных левой и правой моделей и эквивалентной конфигурацией.</summary>
    public static NavisComparisonReport Compare(XDocument document, ModelInput left, ModelInput right, ClashConfiguration configuration)
    {
        var tests = document.Descendants("clashtest").ToArray();
        if (tests.Length != 1) throw new InvalidDataException("Для сравнения нужен XML-отчёт ровно одной проверки.");
        if (!left.Geometry.Header.Complete || !right.Geometry.Header.Complete)
            throw new InvalidDataException("Сравнение требует полных выгрузок обеих моделей.");
        if (left.Geometry.Header.ModelKey == right.Geometry.Header.ModelKey &&
            (!ReferenceEquals(left.Geometry, right.Geometry) || left.Discipline != right.Discipline))
            throw new InvalidDataException("Для проверки модели самой с собой укажите один слепок и один раздел.");
        if (configuration.CellFor(left.Discipline, right.Discipline) is not { Enabled: true })
            throw new InvalidDataException("В конфигурации не включена пара разделов отчёта.");
        var leftIds = left.Geometry.Elements.ToDictionary(element => element.Info.ElementId, element => element.Info.UniqueId);
        var rightIds = right.Geometry.Elements.ToDictionary(element => element.Info.ElementId, element => element.Info.UniqueId);
        var navis = new HashSet<ComparisonPair>();
        var unmapped = new List<string>();
        var resolved = 0;
        foreach (var result in tests[0].Descendants("clashresult"))
        {
            if (string.Equals((string?)result.Attribute("status"), "resolved", StringComparison.OrdinalIgnoreCase)) { resolved++; continue; }
            var objects = result.Element("clashobjects")?.Elements("clashobject").ToArray() ?? [];
            if (objects.Length != 2) throw new InvalidDataException("Запись отчёта не содержит два clashobject.");
            var leftId = ReadId(objects[0]);
            var rightId = ReadId(objects[1]);
            if (!leftIds.TryGetValue(leftId, out var leftUnique) || !rightIds.TryGetValue(rightId, out var rightUnique))
            {
                unmapped.Add($"{(string?)result.Attribute("name")}: {left.Geometry.Header.ModelKey}/{leftId} × {right.Geometry.Header.ModelKey}/{rightId}");
                continue;
            }
            navis.Add(Pair(new(left.Geometry.Header.ModelKey, leftUnique), new(right.Geometry.Header.ModelKey, rightUnique)));
        }
        ModelInput[] models = left.Geometry.Header.ModelKey == right.Geometry.Header.ModelKey ? [left] : [left, right];
        var hits = new ClashEngine(new EngineOptions(8)).Run(models, configuration, CancellationToken.None).Hits
            .Where(hit => (hit.A.ModelKey == left.Geometry.Header.ModelKey && hit.B.ModelKey == right.Geometry.Header.ModelKey) ||
                          (hit.B.ModelKey == left.Geometry.Header.ModelKey && hit.A.ModelKey == right.Geometry.Header.ModelKey)).ToArray();
        var ours = hits.Select(hit => Pair(hit.A, hit.B)).ToHashSet();
        ComparisonPair[] Ordered(IEnumerable<ComparisonPair> pairs) => pairs.OrderBy(pair => pair.A).ThenBy(pair => pair.B).ToArray();
        return new((string?)tests[0].Attribute("name") ?? "", Ordered(navis.Intersect(ours)), Ordered(navis.Except(ours)),
            hits.Where(hit => !navis.Contains(Pair(hit.A, hit.B))).ToArray(), unmapped, resolved);
    }

    /// <summary>CLI: пути к двум TCG и разделы заданы явно; config.json создаётся для сравниваемой проверки вручную.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || args[index] is not ("--report" or "--left" or "--right" or "--left-section" or "--right-section" or "--config"))
                    throw new ArgumentException("Неверные параметры navis.");
                options.Add(args[index], args[index + 1]);
            }
            string Required(string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Не задан {name}.");
            ModelInput Read(string path, string discipline)
            {
                using var geometry = File.OpenRead(path);
                using var parameters = File.OpenRead(Path.ChangeExtension(path, ".tcp"));
                return new(discipline, ModelGeometryFile.Read(geometry), ModelParametersFile.Read(parameters));
            }
            var leftPath = Required("--left");
            var rightPath = Required("--right");
            var leftSection = Required("--left-section");
            var rightSection = Required("--right-section");
            var samePath = Path.GetFullPath(leftPath).Equals(Path.GetFullPath(rightPath), StringComparison.OrdinalIgnoreCase);
            if (samePath && !string.Equals(leftSection, rightSection, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Один файл модели не может относиться к двум разделам.");
            var left = Read(leftPath, leftSection);
            var right = samePath
                ? left : Read(rightPath, Required("--right-section"));
            using var reader = XmlReader.Create(Required("--report"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var report = Compare(XDocument.Load(reader), left, right, ConfigurationJson.Deserialize(File.ReadAllText(Required("--config"))));
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            error.WriteLine($"Совпало: {report.Common.Count}; только Navisworks: {report.OnlyNavis.Count}; только ToadClash: {report.OnlyOurs.Count}; не сопоставлено: {report.Unmapped.Count}.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or XmlException or JsonException or UnauthorizedAccessException)
        {
            error.WriteLine("navis: " + exception.Message);
            return 2;
        }
    }

    private static ComparisonPair Pair(ElementRef first, ElementRef second) => first.CompareTo(second) <= 0 ? new(first, second) : new(second, first);

    private static long ReadId(XElement element)
    {
        var attribute = element.Elements("objectattribute").FirstOrDefault(item => (string?)item.Element("name") is "ID объекта" or "Element ID" or "Object ID");
        var text = (string?)attribute?.Element("value") ?? element.Descendants("smarttag")
            .Where(tag => (string?)tag.Element("name") == "Id IntegerValue").Select(tag => (string?)tag.Element("value")).FirstOrDefault();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id : throw new InvalidDataException("У объекта отчёта нет числового ElementId.");
    }
}
