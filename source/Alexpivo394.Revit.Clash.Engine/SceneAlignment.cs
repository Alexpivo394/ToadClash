using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Проверка, что модели части совмещены, и начало координат сцены. Сцена хранит позиции во float, поэтому всё, что в
///     ней пересекается, должно лежать рядом с её началом: модели, разнесённые на километры, — это несовмещённые модели.
/// </summary>
/// <remarks>
///     «Ядро» модели — габарит центров её элементов между 1-м и 99-м процентилем по каждой оси: одиночный «улетевший»
///     элемент не сдвигает модель. Модели с пересекающимися ядрами (с запасом 10 м) связаны; основная группа — связная
///     компонента с наибольшим числом элементов, все модели вне неё не совмещены.
/// </remarks>
internal static class SceneAlignment
{
    private const double LinkMarginMm = 10_000;
    private const double OriginStepMm = 1000;

    /// <summary>Начало модели годится в начало сцены, если оно не дальше 1 км от центра: шаг float там не больше 0,06 мм.</summary>
    private const double ModelOriginReachMm = 1_000_000;
    private const double LowPercentile = 0.01;
    private const double HighPercentile = 0.99;

    /// <summary>
    ///     Начало сцены — начало модели основной группы, ближайшее к центру её ядер (если оно не дальше 1 км), иначе центр,
    ///     округлённый до метра. Не зависит от порядка моделей.
    /// </summary>
    /// <exception cref="ModelsNotAlignedException">Есть модели вне основной группы.</exception>
    public static (double X, double Y, double Z) Origin(IReadOnlyList<ModelInput> models)
    {
        var cores = models.Select(Core).Where(c => c is not null).Select(c => c!.Value).ToList();
        if (cores.Count == 0) return (0, 0, 0);

        var component = Components(cores);
        // Индексы моделей в группе; при равном числе элементов — группа с наименьшим ключом модели, чтобы не зависеть от порядка.
        var main = Enumerable.Range(0, cores.Count).GroupBy(i => component[i])
            .OrderByDescending(g => g.Sum(i => cores[i].Elements))
            .ThenBy(g => g.Select(i => cores[i].Key).Order(StringComparer.Ordinal).First(), StringComparer.Ordinal)
            .First().Key;
        var mainBox = cores.Where((_, i) => component[i] == main).Select(c => c.Box).Aggregate(Union);

        var misaligned = cores.Where((_, i) => component[i] != main)
            .Select(c => new MisalignedModel(c.Key, Distance(c.Box, mainBox)))
            .OrderBy(m => m.ModelKey, StringComparer.Ordinal)
            .ToList();
        if (misaligned.Count > 0) throw new ModelsNotAlignedException(misaligned);

        var cx = (mainBox.MinX + mainBox.MaxX) / 2;
        var cy = (mainBox.MinY + mainBox.MaxY) / 2;
        var cz = (mainBox.MinZ + mainBox.MaxZ) / 2;

        // Начало ближайшей к центру модели: её сетки идут в сцену без пересчёта (экономия памяти), а точность та же,
        // пока это начало рядом с геометрией.
        var mainKeys = cores.Where((_, i) => component[i] == main).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var nearest = models.Select(m => m.Geometry.Header)
            .Where(h => mainKeys.Contains(h.ModelKey))
            .Select(h => (h.OriginX, h.OriginY, h.OriginZ, h.ModelKey, Distance: Length(h.OriginX - cx, h.OriginY - cy, h.OriginZ - cz)))
            .OrderBy(h => h.Distance).ThenBy(h => h.ModelKey, StringComparer.Ordinal)
            .First();
        if (nearest.Distance <= ModelOriginReachMm) return (nearest.OriginX, nearest.OriginY, nearest.OriginZ);

        return (Round(cx), Round(cy), Round(cz));

        static double Round(double value) => Math.Round(value / OriginStepMm) * OriginStepMm;
    }

    private static double Length(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

    private readonly record struct Box(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    private readonly record struct ModelCore(string Key, int Elements, Box Box);

    private static ModelCore? Core(ModelInput model)
    {
        var header = model.Geometry.Header;
        var elements = model.Geometry.Elements;
        if (elements.Count == 0) return null;

        // Элементы без геометрии (пустая сетка — бесконечный габарит) в ядро не входят.
        var xs = new List<double>(elements.Count);
        var ys = new List<double>(elements.Count);
        var zs = new List<double>(elements.Count);
        foreach (var element in elements)
        {
            var b = element.Bounds;
            var x = header.OriginX + ((double)b.Min.X + b.Max.X) / 2;
            var y = header.OriginY + ((double)b.Min.Y + b.Max.Y) / 2;
            var z = header.OriginZ + ((double)b.Min.Z + b.Max.Z) / 2;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;
            xs.Add(x);
            ys.Add(y);
            zs.Add(z);
        }

        if (xs.Count == 0) return null;
        xs.Sort();
        ys.Sort();
        zs.Sort();
        var low = (int)Math.Floor(LowPercentile * (xs.Count - 1));
        var high = (int)Math.Ceiling(HighPercentile * (xs.Count - 1));
        return new ModelCore(header.ModelKey, xs.Count, new Box(xs[low], ys[low], zs[low], xs[high], ys[high], zs[high]));
    }

    // Номер связной компоненты для каждой модели: объединение-поиск по парам с пересекающимися ядрами.
    private static int[] Components(List<ModelCore> cores)
    {
        var parent = Enumerable.Range(0, cores.Count).ToArray();
        for (var a = 0; a < cores.Count; a++)
        for (var b = a + 1; b < cores.Count; b++)
        {
            if (Distance(cores[a].Box, cores[b].Box) <= LinkMarginMm) parent[Find(a)] = Find(b);
        }

        return Enumerable.Range(0, cores.Count).Select(Find).ToArray();

        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
    }

    private static Box Union(Box a, Box b) => new(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Min(a.MinZ, b.MinZ),
        Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY), Math.Max(a.MaxZ, b.MaxZ));

    // Расстояние между габаритами; 0 — пересекаются.
    private static double Distance(Box a, Box b)
    {
        var dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        var dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        var dz = Math.Max(0, Math.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
