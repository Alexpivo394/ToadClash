using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine.Identity;

/// <summary>Сопоставляет геометрию двух выгрузок с одним ModelKey.</summary>
public static class ElementMatcher
{
    private const long MaximumComparisons = 10_000_000;
    private const int MaximumCandidates = 2_000_000;

    /// <summary>Сохраняет точные UniqueId и предлагает жадные пары по расстоянию центров в общих координатах (мм).</summary>
    /// <remarks>Category/Family/Type сравниваются ordinal. Разница каждого размера строго меньше 1% старого размера;
    /// нулевой размер совпадает только с нулевым. Допуск включителен, ноль разрешён.
    /// Координаты ограничены ±10¹⁵ мм, размер ячейки не меньше 1 мм. Слишком плотный поиск завершается ошибкой.</remarks>
    public static IdentityReport Compare(ModelGeometry oldModel, ModelGeometry newModel, double centerToleranceMm = 50)
    {
        ArgumentNullException.ThrowIfNull(oldModel);
        ArgumentNullException.ThrowIfNull(newModel);
        return Compare(oldModel.Header, oldModel.Elements.Select(e => (e.Info, e.Bounds)), newModel.Header,
            newModel.Elements.Select(e => (e.Info, e.Bounds)), centerToleranceMm);
    }

    /// <summary>То же по оглавлениям выгрузок, без сеток.</summary>
    public static IdentityReport Compare(ModelIndex oldModel, ModelIndex newModel, double centerToleranceMm = 50)
    {
        ArgumentNullException.ThrowIfNull(oldModel);
        ArgumentNullException.ThrowIfNull(newModel);
        return Compare(oldModel.Header, oldModel.Elements.Select(e => (e.Info, e.Bounds)), newModel.Header,
            newModel.Elements.Select(e => (e.Info, e.Bounds)), centerToleranceMm);
    }

    private static IdentityReport Compare(ModelHeader oldHeader, IEnumerable<(ElementInfo Info, Aabb Bounds)> oldModel, ModelHeader newHeader,
        IEnumerable<(ElementInfo Info, Aabb Bounds)> newModel, double centerToleranceMm)
    {
        if (!double.IsFinite(centerToleranceMm) || centerToleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(centerToleranceMm), "Допуск центра должен быть конечным и неотрицательным (мм).");
        if (!string.Equals(oldHeader.ModelKey, newHeader.ModelKey, StringComparison.Ordinal))
            throw new ArgumentException($"ModelKey различаются: '{oldHeader.ModelKey}' и '{newHeader.ModelKey}'. Сопоставление возможно только внутри одной модели.");

        var oldElements = Index(oldHeader, oldModel);
        var newElements = Index(newHeader, newModel);
        var kept = oldElements.Keys.Count(newElements.ContainsKey);
        var cellSize = Math.Max(1, centerToleranceMm);
        var buckets = new Dictionary<Bucket, List<Entry>>();
        foreach (var entry in newElements.Values)
        {
            if (oldElements.ContainsKey(entry.Id)) continue;
            var bucket = GetBucket(entry, cellSize);
            if (!buckets.TryGetValue(bucket, out var entries)) buckets[bucket] = entries = [];
            entries.Add(entry);
        }

        var candidates = new List<Candidate>();
        var comparisons = 0L;
        foreach (var oldEntry in oldElements.Values)
        {
            if (newElements.ContainsKey(oldEntry.Id)) continue;
            var bucket = GetBucket(oldEntry, cellSize);
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                if (!buckets.TryGetValue(bucket with
                    { X = bucket.X + offsetX, Y = bucket.Y + offsetY, Z = bucket.Z + offsetZ }, out var entries)) continue;
                foreach (var newEntry in entries)
                {
                    if (++comparisons > MaximumComparisons)
                        throw new ArgumentException("Слишком плотные данные identity: превышено 10000000 локальных сравнений. Уменьшите выборку геометрии.");
                    if (!SimilarSize(oldEntry.Width, newEntry.Width) ||
                        !SimilarSize(oldEntry.Height, newEntry.Height) ||
                        !SimilarSize(oldEntry.Depth, newEntry.Depth)) continue;
                    var deltaX = oldEntry.X - newEntry.X;
                    var deltaY = oldEntry.Y - newEntry.Y;
                    var deltaZ = oldEntry.Z - newEntry.Z;
                    var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
                    if (distance > centerToleranceMm) continue;
                    if (candidates.Count == MaximumCandidates)
                        throw new ArgumentException("Слишком плотные данные identity: превышено 2000000 кандидатов. Уменьшите выборку геометрии.");
                    candidates.Add(new(oldEntry.Id, newEntry.Id, distance));
                }
            }
        }

        candidates.Sort((left, right) =>
        {
            var order = left.Distance.CompareTo(right.Distance);
            if (order != 0) return order;
            order = StringComparer.Ordinal.Compare(left.OldId, right.OldId);
            return order != 0 ? order : StringComparer.Ordinal.Compare(left.NewId, right.NewId);
        });
        var usedOld = new HashSet<string>(StringComparer.Ordinal);
        var usedNew = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<IdentityMatch>();
        foreach (var candidate in candidates)
        {
            if (usedOld.Contains(candidate.OldId) || usedNew.Contains(candidate.NewId)) continue;
            usedOld.Add(candidate.OldId);
            usedNew.Add(candidate.NewId);
            matches.Add(new(candidate.OldId, candidate.NewId));
        }
        return new(kept, oldElements.Count - kept - matches.Count, newElements.Count - kept - matches.Count, matches.AsReadOnly());
    }

    private static bool SimilarSize(double oldSize, double newSize) =>
        oldSize == 0 ? newSize == 0 : Math.Abs(newSize - oldSize) < oldSize * 0.01;

    private static Dictionary<string, Entry> Index(ModelHeader header, IEnumerable<(ElementInfo Info, Aabb Bounds)> elements)
    {
        ValidateCoordinate(header.OriginX);
        ValidateCoordinate(header.OriginY);
        ValidateCoordinate(header.OriginZ);
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var bounds = element.Bounds;
            var width = (double)bounds.Max.X - bounds.Min.X;
            var height = (double)bounds.Max.Y - bounds.Min.Y;
            var depth = (double)bounds.Max.Z - bounds.Min.Z;
            if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(depth) || width < 0 || height < 0 || depth < 0)
                throw new ArgumentException($"Некорректные габариты элемента '{element.Info.UniqueId}'.");
            var centerX = header.OriginX + ((double)bounds.Min.X + bounds.Max.X) / 2;
            var centerY = header.OriginY + ((double)bounds.Min.Y + bounds.Max.Y) / 2;
            var centerZ = header.OriginZ + ((double)bounds.Min.Z + bounds.Max.Z) / 2;
            ValidateCoordinate(centerX);
            ValidateCoordinate(centerY);
            ValidateCoordinate(centerZ);
            var info = element.Info;
            if (string.IsNullOrEmpty(info.UniqueId)) throw new ArgumentException("Пустой UniqueId элемента геометрии.");
            if (!result.TryAdd(info.UniqueId, new(info.UniqueId, info.Category, info.Family, info.Type,
                    centerX, centerY, centerZ, width, height, depth)))
                throw new ArgumentException($"Повторный UniqueId '{info.UniqueId}' в геометрии модели '{header.ModelKey}'.");
        }
        return result;
    }

    private static void ValidateCoordinate(double coordinate)
    {
        if (!double.IsFinite(coordinate) || Math.Abs(coordinate) > 1e15)
            throw new ArgumentException("Координаты identity должны быть конечными и находиться в пределах ±10¹⁵ мм.");
    }

    private static Bucket GetBucket(Entry entry, double size) => new(entry.Category, entry.Family, entry.Type,
        (long)Math.Floor(entry.X / size), (long)Math.Floor(entry.Y / size), (long)Math.Floor(entry.Z / size));

    private readonly record struct Bucket(string Category, string? Family, string? Type, long X, long Y, long Z);
    private readonly record struct Entry(string Id, string Category, string? Family, string? Type,
        double X, double Y, double Z, double Width, double Height, double Depth);
    private readonly record struct Candidate(string OldId, string NewId, double Distance);
}
