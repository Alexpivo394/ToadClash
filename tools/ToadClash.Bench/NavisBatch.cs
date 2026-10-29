using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadClash.Bench;

/// <summary>Итог сравнения одной проверки Navisworks.</summary>
/// <param name="Left">Число элементов левой стороны во всех моделях.</param>
/// <param name="Right">Число элементов правой стороны во всех моделях.</param>
/// <param name="RuleIgnored">Наши пары, снятые правилами исключения проверки.</param>
/// <param name="ConnectedIgnored">Наши пары соединённых элементов MEP одной модели: продукт их не проверяет.</param>
/// <param name="NavisConnected">Пары отчёта из соединённых элементов MEP: в OnlyNavis не входят.</param>
/// <param name="NestedIgnored">Наши пары вложенных семейств одного изделия (общий корень): продукт их не проверяет.</param>
/// <param name="NavisNested">Пары отчёта внутри одного изделия: в OnlyNavis не входят.</param>
public sealed record NavisTestResult(string Test, int Left, int Right, IReadOnlyList<ComparisonPair> Common,
    IReadOnlyList<ComparisonPair> OnlyNavis, IReadOnlyList<ClashHit> OnlyOurs, IReadOnlyList<string> Unmapped,
    int IgnoredResolved, int RuleIgnored, int ConnectedIgnored, int NavisConnected, int NestedIgnored, int NavisNested);

/// <summary>Принадлежность элементов всех моделей наборам Navisworks; каждый набор вычисляется один раз.</summary>
public sealed class NavisMembership(IReadOnlyList<ModelInput> models)
{
    private readonly Dictionary<NavisSet, HashSet<ElementRef>> _members = new();

    /// <summary>Модели сравнения.</summary>
    public IReadOnlyList<ModelInput> Models { get; } = models;

    /// <summary>Входят ли оба элемента пары в одно изделие: одна модель и общий корень вложенных семейств.</summary>
    public bool SameProduct(ComparisonPair pair) =>
        pair.A.ModelKey == pair.B.ModelKey && Product(pair.A) == Product(pair.B);

    private string Product(ElementRef element) => Roots.TryGetValue(element, out var root) ? root : element.UniqueId;

    // Корни вложенных элементов; элемент без корня - сам себе изделие.
    private Dictionary<ElementRef, string> Roots => field ??= Models
        .SelectMany(model => model.Geometry.Elements
            .Where(element => element.Info.RootComponentUniqueId is not null)
            .Select(element => (Key: new ElementRef(model.Geometry.Header.ModelKey, element.Info.UniqueId), Root: element.Info.RootComponentUniqueId!)))
        .ToDictionary(x => x.Key, x => x.Root);

    /// <summary>Пары соединённых элементов MEP одной модели по ConnectedUniqueIds в любую сторону.</summary>
    public HashSet<ComparisonPair> Connected => field ??= Models
        .SelectMany(model => model.Geometry.Elements.SelectMany(element => element.Info.ConnectedUniqueIds.Select(other =>
            NavisCompare.Pair(new ElementRef(model.Geometry.Header.ModelKey, element.Info.UniqueId),
                new ElementRef(model.Geometry.Header.ModelKey, other)))))
        .ToHashSet();

    /// <summary>Элементы набора.</summary>
    public HashSet<ElementRef> Of(NavisSet set)
    {
        if (_members.TryGetValue(set, out var members)) return members;
        members = [];
        foreach (var model in Models)
        {
            var key = model.Geometry.Header.ModelKey;
            foreach (var element in model.Geometry.Elements)
            {
                if (set.Matches(new NavisItem(key, element.Info, model.Parameters.For(element.Info.UniqueId))))
                    members.Add(new ElementRef(key, element.Info.UniqueId));
            }
        }

        _members.Add(set, members);
        return members;
    }
}

/// <summary>
///     Разовое сравнение этапа 0: каждая проверка файла настроек Navisworks воспроизводится на выгрузках всех моделей
///     и сравнивается с XML-отчётом того же имени. Стороны проверки - объединения её наборов по всем моделям, как в
///     сводной модели Navisworks; самопроверка стороны (selfintersect) не выполняется. Правило исключения снимает пару,
///     если один элемент входит в первый набор правила, а второй - во второй. Пары соединённых элементов MEP одной модели
///     и пары вложенных семейств одного изделия снимаются, как в продукте, и считаются отдельно. Режим
///     hard_conservative и составные объекты не воспроизводятся.
/// </summary>
public static class NavisBatch
{
    private const string LeftSide = "L";
    private const string RightSide = "R";
    private const char SideSeparator = '|';

    /// <summary>Сравнивает одну проверку; без отчёта все наши пары попадают в OnlyOurs.</summary>
    public static NavisTestResult Compare(XElement clashTest, NavisSelection selection, NavisMembership membership, XDocument? report)
    {
        var name = (string)clashTest.Attribute("name")!;
        var left = SideMembers(clashTest.Element("left"));
        var right = SideMembers(clashTest.Element("right"));
        var toleranceMm = double.Parse((string)clashTest.Attribute("tolerance")!, CultureInfo.InvariantCulture) * 304.8;

        var inputs = new List<ModelInput>();
        foreach (var model in membership.Models)
        {
            Add(model, left, LeftSide);
            Add(model, right, RightSide);
        }

        var configuration = ConfigurationDefaults.FromDisciplines(
            [AllOf(LeftSide), AllOf(RightSide)], [new DisciplineCell(LeftSide, RightSide, true, toleranceMm)], [], ignoreConnectedMep: false);
        var rules = Rules(clashTest);
        var ours = new Dictionary<ComparisonPair, ClashHit>();
        var ruleIgnored = new HashSet<ComparisonPair>();
        var connectedIgnored = new HashSet<ComparisonPair>();
        var nestedIgnored = new HashSet<ComparisonPair>();
        if (inputs.Count > 0)
        {
            foreach (var hit in new ClashEngine(new EngineOptions()).Run(inputs, configuration, CancellationToken.None).Hits)
            {
                var a = Original(hit.A);
                var b = Original(hit.B);
                if (a == b) continue;
                var pair = NavisCompare.Pair(a, b);
                if (ours.ContainsKey(pair)) continue;
                if (membership.SameProduct(pair))
                {
                    nestedIgnored.Add(pair);
                    continue;
                }

                if (membership.Connected.Contains(pair))
                {
                    connectedIgnored.Add(pair);
                    continue;
                }

                if (rules.Any(rule => (rule.A.Contains(a) && rule.B.Contains(b)) || (rule.A.Contains(b) && rule.B.Contains(a))))
                {
                    ruleIgnored.Add(pair);
                    continue;
                }

                ours.Add(pair, hit with { A = pair.A, B = pair.B });
            }
        }

        var (navis, unmapped, resolved) = report is null ? ([], [], 0) : ReadReport(report, membership.Models);
        ComparisonPair[] Ordered(IEnumerable<ComparisonPair> pairs) => pairs.OrderBy(pair => pair.A).ThenBy(pair => pair.B).ToArray();
        return new NavisTestResult(name, left.Count, right.Count, Ordered(navis.Intersect(ours.Keys)), Ordered(navis.Except(ours.Keys).Where(pair => !membership.Connected.Contains(pair) && !membership.SameProduct(pair))),
            ours.Where(entry => !navis.Contains(entry.Key)).Select(entry => entry.Value).ToArray(), unmapped, resolved, ruleIgnored.Count, connectedIgnored.Count,
            navis.Count(membership.Connected.Contains), nestedIgnored.Count, navis.Count(membership.SameProduct));

        HashSet<ElementRef> SideMembers(XElement? side)
        {
            var locator = side?.Element("clashselection")?.Element("locator")?.Value ?? "";
            var members = new HashSet<ElementRef>();
            foreach (var path in locator.Split([';'], StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var set in selection.Resolve(path)) members.UnionWith(membership.Of(set));
            }

            return members;
        }

        void Add(ModelInput model, HashSet<ElementRef> members, string side)
        {
            var key = model.Geometry.Header.ModelKey;
            var elements = model.Geometry.Elements.Where(element => members.Contains(new ElementRef(key, element.Info.UniqueId))).ToList();
            if (elements.Count == 0) return;
            var header = model.Geometry.Header with { ModelKey = key + SideSeparator + side };
            inputs.Add(new ModelInput(side, new ModelGeometry(header, elements, []), model.Parameters));
        }

        List<(HashSet<ElementRef> A, HashSet<ElementRef> B)> Rules(XElement test)
        {
            var result = new List<(HashSet<ElementRef>, HashSet<ElementRef>)>();
            foreach (var rule in test.Element("rules")?.Elements("rule") ?? [])
            {
                if ((string?)rule.Attribute("enabled") != "1") continue;
                if ((string?)rule.Attribute("creator") != "LcOdpClashIgnoreSpecSelectionSetsPlugin")
                    throw new InvalidDataException($"Проверка {name}: правило {(string?)rule.Attribute("creator")} не поддерживается.");
                var sets = rule.Descendants("ruleparam")
                    .Select(param => (string)param.Descendants("name").Single().Attribute("internal")!)
                    .Select(RuleMembers)
                    .ToArray();
                if (sets.Length != 2) throw new InvalidDataException($"Проверка {name}: правило без двух наборов.");
                result.Add((sets[0], sets[1]));
            }

            return result;
        }

        // Папка и набор разделены переводом строки, но разбор XML-атрибута заменяет его пробелом,
        // а имена наборов сами содержат пробелы, поэтому путь ищется среди известных наборов.
        HashSet<ElementRef> RuleMembers(string internalName)
        {
            var normalized = internalName.Replace('\r', ' ').Replace('\n', ' ');
            var sets = selection.Sets.Where(set => string.Join(" ", set.Segments) == normalized).ToArray();
            if (sets.Length != 1)
                throw new InvalidDataException($"Проверка {name}: набор правила «{internalName}» найден {sets.Length} раз.");
            return membership.Of(sets[0]);
        }
    }

    /// <summary>CLI: --settings xml --reports каталог --snapshots каталог --out каталог [--test имя].</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < args.Length; index += 2) options[args[index].TrimStart('-')] = args[index + 1];
        string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Не задан --{key}.");

        var settings = Load(Required("settings"));
        var selection = NavisSelection.Parse(settings.Descendants("selectionsets").Single());
        var models = LoadModels(Required("snapshots"), error);
        var membership = new NavisMembership(models);
        var outDirectory = Required("out");
        Directory.CreateDirectory(outDirectory);
        var only = options.TryGetValue("test", out var test) ? test : null;

        output.WriteLine("test\tleft\tright\tnavis\tours\tcommon\tonlyNavis\tonlyOurs\tunmapped\truleIgnored\tseconds");
        foreach (var clashTest in settings.Descendants("clashtest"))
        {
            var name = (string)clashTest.Attribute("name")!;
            if (only is not null && name != only) continue;
            var reportPath = Path.Combine(Required("reports"), name + ".xml");
            var report = File.Exists(reportPath) ? Load(reportPath) : null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = Compare(clashTest, selection, membership, report);
            output.WriteLine(string.Join("\t", name, result.Left, result.Right, result.Common.Count + result.OnlyNavis.Count + result.NavisConnected + result.NavisNested,
                result.Common.Count + result.OnlyOurs.Count, result.Common.Count, result.OnlyNavis.Count, result.OnlyOurs.Count,
                result.Unmapped.Count, result.RuleIgnored, result.ConnectedIgnored, result.NavisConnected, result.NestedIgnored, result.NavisNested, clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));
            output.Flush();
            WriteDetails(Path.Combine(outDirectory, name + ".tsv"), result, models);
        }

        return 0;
    }

    private static DisciplineSet AllOf(string side) => new(Guid.NewGuid(), side, side, [new ConditionGroup([])]);

    private static ElementRef Original(ElementRef side) => side with { ModelKey = side.ModelKey[..side.ModelKey.LastIndexOf(SideSeparator)] };

    private static (HashSet<ComparisonPair> Pairs, List<string> Unmapped, int Resolved) ReadReport(XDocument report, IReadOnlyList<ModelInput> models)
    {
        var lookups = models.Select(model => model.Geometry.Elements.ToDictionary(element => element.Info.ElementId)).ToArray();
        var scale = NavisCompare.UnitsToMillimeters((string?)report.Root?.Attribute("units"));
        var pairs = new HashSet<ComparisonPair>();
        var unmapped = new List<string>();
        var resolved = 0;
        foreach (var result in report.Descendants("clashresult"))
        {
            if (string.Equals((string?)result.Attribute("status"), "resolved", StringComparison.OrdinalIgnoreCase)) { resolved++; continue; }
            var objects = result.Element("clashobjects")?.Elements("clashobject").ToArray() ?? [];
            var point = NavisCompare.ReadPoint(result, scale);
            var ids = objects.Select(NavisCompare.ReadId).ToArray();
            var refs = ids.Select(id => Resolve(id, point)).ToArray();
            if (refs.Length != 2 || refs.Any(item => item is null))
            {
                unmapped.Add($"{(string?)result.Attribute("name")}: {string.Join(" × ", ids.Select(id => id?.ToString(CultureInfo.InvariantCulture) ?? "нет ID"))}");
                continue;
            }

            pairs.Add(NavisCompare.Pair(refs[0]!.Value, refs[1]!.Value));
        }

        return (pairs, unmapped, resolved);

        // ElementId уникален только внутри модели: из нескольких моделей берётся элемент, ближайший к точке коллизии.
        ElementRef? Resolve(long? id, (double X, double Y, double Z)? point)
        {
            if (id is not { } value) return null;
            var candidates = Enumerable.Range(0, models.Count).Where(index => lookups[index].ContainsKey(value)).ToArray();
            if (candidates.Length == 0) return null;
            var chosen = point is null ? candidates[0]
                : candidates.OrderBy(index => NavisCompare.Distance(models[index].Geometry.Header, lookups[index][value].Bounds, point.Value)).First();
            return new ElementRef(models[chosen].Geometry.Header.ModelKey, lookups[chosen][value].Info.UniqueId);
        }
    }

    private static IReadOnlyList<ModelInput> LoadModels(string directory, TextWriter log)
    {
        var files = Directory.EnumerateFiles(directory, "model.tcg", SearchOption.AllDirectories)
            .Where(file => !Path.GetDirectoryName(file)!.EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        var models = new ModelInput[files.Length];
        Parallel.For(0, files.Length, index =>
        {
            using var geometry = File.OpenRead(files[index]);
            using var parameters = File.OpenRead(Path.ChangeExtension(files[index], ".tcp"));
            var model = ModelGeometryFile.Read(geometry);
            if (!model.Header.Complete) throw new InvalidDataException($"Неполная выгрузка {files[index]}.");
            models[index] = new ModelInput(model.Header.ModelKey, model, ModelParametersFile.Read(parameters));
        });
        foreach (var model in models)
            log.WriteLine($"{model.Geometry.Header.ModelKey}: {model.Geometry.Elements.Count} элементов");
        if (models.Select(model => model.Geometry.Header.ModelKey).Distinct(StringComparer.Ordinal).Count() != models.Length)
            throw new InvalidDataException("В каталоге несколько выгрузок одной модели.");
        return models;
    }

    private static XDocument Load(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static void WriteDetails(string path, NavisTestResult result, IReadOnlyList<ModelInput> models)
    {
        var infos = models.SelectMany(model => model.Geometry.Elements.Select(element => (Ref: new ElementRef(model.Geometry.Header.ModelKey, element.Info.UniqueId), element.Info)))
            .ToDictionary(entry => entry.Ref, entry => entry.Info);
        var text = new StringBuilder("kind\tmodelA\tidA\tcategoryA\tfamilyA\tmodelB\tidB\tcategoryB\tfamilyB\tdepthMm\tx\ty\tz\n");
        void Row(string kind, ElementRef a, ElementRef b, ClashHit? hit)
        {
            var x = infos[a];
            var y = infos[b];
            text.Append(string.Join("\t", kind, a.ModelKey, x.ElementId, x.Category, x.Family, b.ModelKey, y.ElementId, y.Category, y.Family,
                hit?.DepthMm.ToString("0.0", CultureInfo.InvariantCulture), hit?.PointX.ToString("0", CultureInfo.InvariantCulture),
                hit?.PointY.ToString("0", CultureInfo.InvariantCulture), hit?.PointZ.ToString("0", CultureInfo.InvariantCulture))).Append('\n');
        }

        foreach (var pair in result.Common) Row("common", pair.A, pair.B, null);
        foreach (var pair in result.OnlyNavis) Row("onlyNavis", pair.A, pair.B, null);
        foreach (var hit in result.OnlyOurs) Row("onlyOurs", hit.A, hit.B, hit);
        foreach (var line in result.Unmapped) text.Append("unmapped\t").Append(line).Append('\n');
        File.WriteAllText(path, text.ToString());
    }
}
