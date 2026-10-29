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
        if (configuration.CellFor(ConfigurationDefaults.DisciplineGroupId(left.Discipline), ConfigurationDefaults.DisciplineGroupId(right.Discipline)) is not { Enabled: true })
            throw new InvalidDataException("В конфигурации не включена пара разделов отчёта.");
        ModelInput[] sides = ReferenceEquals(left, right) ? [left] : [left, right];
        var lookups = sides.Select(side => side.Geometry.Elements.ToDictionary(element => element.Info.ElementId)).ToArray();
        var scale = UnitsToMillimeters((string?)document.Root?.Attribute("units"));
        var navis = new HashSet<ComparisonPair>();
        var unmapped = new List<string>();
        var resolved = 0;
        foreach (var result in tests[0].Descendants("clashresult"))
        {
            if (string.Equals((string?)result.Attribute("status"), "resolved", StringComparison.OrdinalIgnoreCase)) { resolved++; continue; }
            var objects = result.Element("clashobjects")?.Elements("clashobject").ToArray() ?? [];
            if (objects.Length != 2) throw new InvalidDataException("Запись отчёта не содержит два clashobject.");
            var point = ReadPoint(result, scale);
            var firstId = ReadId(objects[0]);
            var secondId = ReadId(objects[1]);
            var first = Resolve(firstId, 0);
            var second = Resolve(secondId, 1);
            if (first is null || second is null)
            {
                unmapped.Add($"{(string?)result.Attribute("name")}: {firstId?.ToString() ?? "нет ID"} × {secondId?.ToString() ?? "нет ID"}");
                continue;
            }
            navis.Add(Pair(first.Value, second.Value));

            // Отчёт не хранит файл-источник объекта, а ElementId разных моделей могут совпасть: при двух кандидатах
            // берётся элемент, ближайший к точке коллизии; без точки или при равенстве - по порядку объектов в записи.
            ElementRef? Resolve(long? maybeId, int position)
            {
                if (maybeId is not { } id) return null;
                var candidates = Enumerable.Range(0, sides.Length)
                    .Where(index => lookups[index].ContainsKey(id))
                    .Select(index => (Side: index, Element: lookups[index][id]))
                    .ToArray();
                if (candidates.Length == 0) return null;
                var chosen = candidates.Length == 1 ? candidates[0]
                    : point is null ? candidates.Single(candidate => candidate.Side == position)
                    : candidates.OrderBy(candidate => Distance(sides[candidate.Side].Geometry.Header, candidate.Element.Bounds, point.Value))
                        .ThenBy(candidate => candidate.Side == position ? 0 : 1).First();
                return new ElementRef(sides[chosen.Side].Geometry.Header.ModelKey, chosen.Element.Info.UniqueId);
            }
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

    internal static double UnitsToMillimeters(string? units) => units switch
    {
        null or "m" => 1000,
        "mm" => 1,
        "cm" => 10,
        "ft" => 304.8,
        "in" => 25.4,
        _ => throw new InvalidDataException($"Неизвестные единицы отчёта: {units}.")
    };

    internal static (double X, double Y, double Z)? ReadPoint(XElement result, double scale)
    {
        if (result.Element("clashpoint")?.Element("pos3f") is not { } position) return null;
        double Read(string name) => double.Parse((string)position.Attribute(name)!, CultureInfo.InvariantCulture) * scale;
        return (Read("x"), Read("y"), Read("z"));
    }

    /// <summary>Расстояние от точки (мм, общая система) до габарита элемента в абсолютных координатах.</summary>
    internal static double Distance(ModelHeader header, Aabb bounds, (double X, double Y, double Z) point)
    {
        static double Axis(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;
        var dx = Axis(point.X, bounds.Min.X + header.OriginX, bounds.Max.X + header.OriginX);
        var dy = Axis(point.Y, bounds.Min.Y + header.OriginY, bounds.Max.Y + header.OriginY);
        var dz = Axis(point.Z, bounds.Min.Z + header.OriginZ, bounds.Max.Z + header.OriginZ);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    internal static ComparisonPair Pair(ElementRef first, ElementRef second) => first.CompareTo(second) <= 0 ? new(first, second) : new(second, first);

    /// <summary>
    ///     ElementId объекта. Smarttag «Id IntegerValue» - Id элемента Revit (сверено по положению на реальных моделях);
    ///     objectattribute «ID объекта» в отчётах заказчика с ним часто не совпадает и используется только при пустом smarttag.
    ///     Без числового ID запись считается несопоставленной.
    /// </summary>
    internal static long? ReadId(XElement element)
    {
        var smarttag = element.Descendants("smarttag")
            .Where(tag => (string?)tag.Element("name") == "Id IntegerValue").Select(tag => (string?)tag.Element("value")).FirstOrDefault();
        var attribute = element.Elements("objectattribute").FirstOrDefault(item => (string?)item.Element("name") is "ID объекта" or "Element ID" or "Object ID");
        var text = string.IsNullOrWhiteSpace(smarttag) ? (string?)attribute?.Element("value") : smarttag;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
