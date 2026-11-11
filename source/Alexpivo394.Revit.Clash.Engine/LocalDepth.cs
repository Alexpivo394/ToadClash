namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Местная глубина проникновения по точкам внутри пересечения двух замкнутых тел. Сдвиг через точку x по
///     направлению d - от x по d до выхода из B и по −d до выхода из A: на столько нужно сдвинуть A по d, чтобы на
///     прямой через x тела разошлись. Глубина у точки x - наименьший по направлениям наибольший сдвиг среди точек её
///     окрестности; глубина пары - наибольшая по точкам, но не больше <c>limit</c>.
/// </summary>
/// <remarks>
///     Оценка по зоне (<see cref="PenetrationDepth" />) видит тела целиком и не знает о вырезах: перегородка, заходящая
///     в край отверстия пола на 1 мм, получала глубину толщиной пола. Окрестность радиусом <c>limit</c> не захватывает
///     другую сторону выреза, и нахлёст у края - это его толщина. Тело, окружённое другим (труба в стене, штукатурка в
///     стяжке), ни по одному направлению не выходит ближе <c>limit</c>. Направления - нормали пересекающихся граней
///     пары, их попарные суммы и разности (нахлёст буквой Г у торца) и координатные оси, в обе стороны. Выход из
///     элемента - по объединению его тел: внутренние грани касающихся и вложенных тел луч проходит.
/// </remarks>
internal static class LocalDepth
{
    /// <summary>Сдвиг точки поверхности внутрь обоих тел, мм: точка пересечения лежит на обеих поверхностях.</summary>
    public const double Nudge = 0.05;

    /// <summary>Наибольшее число нормалей пересекающихся граней пары, общих для всех точек.</summary>
    public const int MaxPairNormals = 16;

    /// <summary>Из скольких первых нормалей пары строятся диагонали направлений.</summary>
    private const int MaxDiagonalNormals = 6;

    // Наклон направлений, чтобы луч не шёл точно вдоль граней и рёбер ортогональных сеток.
    private static readonly Vec3d Tilt = new(1.3e-4, 2.9e-4, 3.7e-4);

    private static readonly Vec3d[] Axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];

    // Направление луча проверки «внутри»: не вдоль осей, чтобы не идти по рёбрам ортогональных сеток.
    private static readonly Vec3d InsideDirection = new Vec3d(0.5773, 0.5774, 0.5775).Normalized();

    // Зазор, через который интервалы соседних тел элемента склеиваются, мм: тела касаются гранями.
    private const double JoinGap = 0.01;

    // Доли пути внутрь общей части, в которых берутся глубокие точки.
    private static readonly double[] InwardShares = [0.25, 0.5, 0.75];

    /// <summary>Точка у поверхностей, сдвинутая внутрь обоих тел, и наружные нормали A и B в ней (нулевые - неизвестны).</summary>
    public readonly record struct Sample(Vec3d Point, Vec3d NormalA, Vec3d NormalB);

    /// <summary>
    ///     Глубина по точкам <paramref name="samples" />, не больше <paramref name="limit" />. Точки, не лежащие в обоих
    ///     телах, не берутся. Если таких не осталось: при <paramref name="surfaceContact" /> пересечение тоньше сдвига
    ///     точек (касание с погрешностью) - 0, иначе судить не по чему - <paramref name="limit" />.
    /// </summary>
    public static double Estimate(MeshBvh bvhA, MeshBvh bvhB, IReadOnlyList<Sample> samples, IReadOnlyList<Vec3d> pairNormals, bool surfaceContact,
        double limit, Rays scratch)
    {
        var points = new List<Vec3d>(samples.Count);
        var inside = new List<Sample>(samples.Count);
        foreach (var sample in samples)
        {
            if (!Inside(bvhA, sample.Point, scratch) || !Inside(bvhB, sample.Point, scratch)) continue;
            inside.Add(sample);
            points.Add(sample.Point);
        }

        if (points.Count == 0) return surfaceContact ? 0 : limit;

        // Точки линий пересечения лежат у поверхностей; глубокие части общей части (середина плиты, сквозь которую идёт
        // труба) дают точки по пути внутрь вдоль нормали каждого тела, пока путь в обоих телах.
        foreach (var sample in inside)
        {
            Inward(sample.Point, sample.NormalA);
            Inward(sample.Point, sample.NormalB);
        }

        var directions = new List<Vec3d>();
        var normals = Math.Min(pairNormals.Count, MaxPairNormals);
        for (var i = 0; i < normals; i++) AddBoth(pairNormals[i]);
        // Диагонали: нахлёст буквой Г у торца (сбоку и с торца) уходит только между двумя нормалями.
        var diagonals = Math.Min(normals, MaxDiagonalNormals);
        for (var i = 0; i < diagonals; i++)
        for (var j = i + 1; j < diagonals; j++)
        {
            var sum = pairNormals[i] + pairNormals[j];
            var difference = pairNormals[i] - pairNormals[j];
            if (sum.LengthSquared > 0.01) AddBoth(sum.Normalized());
            if (difference.LengthSquared > 0.01) AddBoth(difference.Normalized());
        }

        foreach (var axis in Axes) AddBoth(axis);

        var chords = new double[points.Count * directions.Count];
        Array.Fill(chords, double.NaN);
        var neighbours = Neighbours(points, limit);

        var best = 0.0;
        for (var x = 0; x < points.Count; x++)
        {
            // Наименьшая по направлениям наибольшая хорда окрестности; не больше лучшей - точка ничего не изменит.
            var depth = limit;
            for (var d = 0; d < directions.Count && depth > best; d++)
            {
                var longest = 0.0;
                foreach (var y in neighbours[x])
                {
                    longest = Math.Max(longest, Chord(y, d));
                    if (longest >= depth) break;
                }

                depth = Math.Min(depth, longest);
            }

            if (depth > best) best = depth;
            if (best >= limit) return limit;
        }

        return best;

        void Inward(in Vec3d point, in Vec3d normal)
        {
            if (normal.LengthSquared == 0) return;
            var direction = (normal * -1 + Tilt).Normalized();
            var length = Math.Min(Exit(bvhA, point, direction, limit, scratch), Exit(bvhB, point, direction, limit, scratch));
            if (length <= 2 * Nudge) return;
            foreach (var share in InwardShares) points.Add(point + direction * (length * share));
        }

        void AddBoth(in Vec3d direction)
        {
            directions.Add((direction + Tilt).Normalized());
            directions.Add((direction * -1 + Tilt).Normalized());
        }

        double Chord(int point, int direction)
        {
            ref var chord = ref chords[point * directions.Count + direction];
            if (double.IsNaN(chord))
            {
                var d = directions[direction];
                chord = Math.Min(limit, Exit(bvhB, points[point], d, limit, scratch) + Exit(bvhA, points[point], d * -1, limit, scratch));
            }

            return chord;
        }
    }

    // Точки не дальше radius от каждой точки (сама точка - тоже), по сетке с ячейкой radius.
    private static List<int>[] Neighbours(List<Vec3d> points, double radius)
    {
        var cells = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < points.Count; i++)
        {
            var key = Cell(points[i], radius);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }

        var result = new List<int>[points.Count];
        var radiusSquared = radius * radius;
        for (var i = 0; i < points.Count; i++)
        {
            var (cx, cy, cz) = Cell(points[i], radius);
            var near = new List<int>();
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                foreach (var j in list)
                    if ((points[j] - points[i]).LengthSquared <= radiusSquared) near.Add(j);
            }

            result[i] = near;
        }

        return result;
    }

    private static (long, long, long) Cell(in Vec3d p, double size) =>
        ((long)Math.Floor(p.X / size), (long)Math.Floor(p.Y / size), (long)Math.Floor(p.Z / size));

    /// <summary>
    ///     Лежит ли точка в элементе: внутри хотя бы одной его оболочки (чётность пересечений луча с этой оболочкой).
    ///     Элемент из касающихся или вложенных тел так - одно тело.
    /// </summary>
    private static bool Inside(MeshBvh bvh, in Vec3d point, Rays rays)
    {
        Hits(bvh, point, InsideDirection, rays);
        var hits = rays.Hits;
        for (var i = 0; i < hits.Count;)
        {
            var shell = hits[i].Shell;
            var count = 0;
            for (; i < hits.Count && hits[i].Shell == shell; i++) count++;
            if (count % 2 == 1) return true;
        }

        return false;
    }

    /// <summary>
    ///     Расстояние по лучу от точки внутри элемента до выхода из него, не больше limit: интервалы внутри каждой
    ///     оболочки склеиваются, поэтому луч проходит внутренние грани касающихся и вложенных тел. Точка вне элемента - 0.
    /// </summary>
    private static double Exit(MeshBvh bvh, in Vec3d origin, in Vec3d direction, double limit, Rays rays)
    {
        Hits(bvh, origin, direction, rays);
        var hits = rays.Hits;
        var spans = rays.Spans;
        spans.Clear();
        for (var i = 0; i < hits.Count;)
        {
            var shell = hits[i].Shell;
            var first = i;
            for (; i < hits.Count && hits[i].Shell == shell; i++) { }
            var count = i - first;
            // Нечётное число пересечений оболочки до бесконечности - начало луча внутри неё.
            var start = count % 2 == 1 ? 0.0 : double.NaN;
            for (var k = first; k < i; k++)
            {
                if (double.IsNaN(start))
                {
                    start = hits[k].T;
                    continue;
                }

                spans.Add((start, hits[k].T));
                start = double.NaN;
            }
        }

        spans.Sort(static (x, y) => x.Start.CompareTo(y.Start));
        var end = 0.0;
        var inside = false;
        foreach (var (start, stop) in spans)
        {
            if (start > end + JoinGap) break;
            if (start <= 0) inside = true;
            if (!inside) break;
            end = Math.Max(end, stop);
            if (end >= limit) return limit;
        }

        return inside ? Math.Min(end, limit) : 0;
    }

    // Пересечения бесконечного луча с треугольниками: расстояние > 0 и оболочка, по оболочке, затем по расстоянию.
    private static void Hits(MeshBvh bvh, in Vec3d origin, in Vec3d direction, Rays rays)
    {
        var triangles = rays.Triangles;
        var hits = rays.Hits;
        triangles.Clear();
        hits.Clear();
        bvh.QueryRay(origin, direction, triangles);
        foreach (var t in triangles)
        {
            bvh.Vertices(t, out var v0, out var v1, out var v2);
            var e1 = v1 - v0;
            var e2 = v2 - v0;
            var pv = Vec3d.Cross(direction, e2);
            var det = Vec3d.Dot(e1, pv);
            if (Math.Abs(det) <= 1e-12 * Math.Sqrt(e1.LengthSquared * e2.LengthSquared)) continue;
            var inv = 1 / det;
            var tv = origin - v0;
            var u = Vec3d.Dot(tv, pv) * inv;
            if (u < 0 || u > 1) continue;
            var qv = Vec3d.Cross(tv, e1);
            var v = Vec3d.Dot(direction, qv) * inv;
            if (v < 0 || u + v > 1) continue;
            var distance = Vec3d.Dot(e2, qv) * inv;
            if (distance > 0) hits.Add((distance, bvh.ShellOf(t)));
        }

        hits.Sort(static (x, y) => x.Shell != y.Shell ? x.Shell.CompareTo(y.Shell) : x.T.CompareTo(y.T));
    }

    /// <summary>Буферы лучей одного потока.</summary>
    public sealed class Rays
    {
        public List<int> Triangles { get; } = [];
        public List<(double T, int Shell)> Hits { get; } = [];
        public List<(double Start, double Stop)> Spans { get; } = [];
    }
}
