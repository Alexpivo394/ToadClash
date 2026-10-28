namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Пересечение двух треугольников по схеме Мёллера: расстояния вершин до плоскости другого треугольника,
///     затем перекрытие отрезков пересечения с плоскостями на общей прямой.
/// </summary>
/// <remarks>
///     Пересечением не считаются: касание в точке, отрезок нулевой длины, касание по ребру с обеих сторон
///     (общее ребро), копланарные треугольники со встречными нормалями (грани касаются).
///     Копланарные треугольники с сонаправленными нормалями и общей площадью больше
///     <see cref="CoplanarAreaEpsilon"/> засчитываются: тела лежат за одной плоскостью и перекрываются
///     (дубликаты, перекрытие заподлицо).
///     Треугольник, который лежит ребром на другом и не пересекает его плоскость, засчитывается, только если
///     он уходит за плоскость против нормали другого (внутрь тела): так находятся перекрытия заподлицо,
///     а касание снаружи - нет. Оба правила опираются на внешнюю ориентацию; для замкнутых сеток её
///     обеспечивает <see cref="MeshTopology"/>, незамкнутые сетки берутся как есть.
/// </remarks>
internal static class TriangleIntersection
{
    /// <summary>
    ///     Допуск расстояния до плоскости и длины отрезка пересечения, мм. Равен шагу сшивки выгрузки: грани соседних
    ///     элементов триангулируются независимо и при касании расходятся на микроны (воздуховод в проёме стены).
    /// </summary>
    public const double PlaneEpsilon = 1e-2;

    /// <summary>Наименьшая общая площадь копланарных треугольников, которая считается пересечением, мм².</summary>
    public const double CoplanarAreaEpsilon = 1e-2;

    /// <summary>Порог вырожденности: квадрат длины векторного произведения сторон, как в <see cref="ClashScene"/>.</summary>
    public const double DegenerateAreaSquared = 1e-12;

    /// <summary>
    ///     Пересекаются ли треугольники. При пересечении <paramref name="p"/> и <paramref name="q"/> - концы отрезка,
    ///     для копланарного перекрытия - углы габарита общей части.
    /// </summary>
    public static bool Intersect(
        in Vec3d a0, in Vec3d a1, in Vec3d a2,
        in Vec3d b0, in Vec3d b1, in Vec3d b2,
        out Vec3d p, out Vec3d q)
    {
        p = default;
        q = default;

        var nb = Vec3d.Cross(b1 - b0, b2 - b0);
        var nbLength = nb.LengthSquared;
        if (nbLength <= DegenerateAreaSquared) return false;
        nb *= 1 / Math.Sqrt(nbLength);
        var da0 = Snap(Vec3d.Dot(nb, a0 - b0));
        var da1 = Snap(Vec3d.Dot(nb, a1 - b0));
        var da2 = Snap(Vec3d.Dot(nb, a2 - b0));
        if (StrictlyOneSide(da0, da1, da2)) return false;
        if (da0 == 0 && da1 == 0 && da2 == 0) return Coplanar(a0, a1, a2, b0, b1, b2, nb, out p, out q);

        var na = Vec3d.Cross(a1 - a0, a2 - a0);
        var naLength = na.LengthSquared;
        if (naLength <= DegenerateAreaSquared) return false;
        na *= 1 / Math.Sqrt(naLength);
        var db0 = Snap(Vec3d.Dot(na, b0 - a0));
        var db1 = Snap(Vec3d.Dot(na, b1 - a0));
        var db2 = Snap(Vec3d.Dot(na, b2 - a0));
        if (StrictlyOneSide(db0, db1, db2)) return false;

        var aCrosses = Crosses(da0, da1, da2);
        var bCrosses = Crosses(db0, db1, db2);
        if (!aCrosses && !(bCrosses && RestsBehind(da0, da1, da2))) return false;
        if (!bCrosses && !(aCrosses && RestsBehind(db0, db1, db2))) return false;

        PlaneSegment(a0, a1, a2, da0, da1, da2, out var sa, out var ea);
        PlaneSegment(b0, b1, b2, db0, db1, db2, out var sb, out var eb);

        // Оба отрезка лежат на прямой пересечения плоскостей; параметризуем её отрезком A.
        var axis = ea - sa;
        var length = Math.Sqrt(axis.LengthSquared);
        if (length <= PlaneEpsilon) return false;
        axis *= 1 / length;
        var tb0 = Vec3d.Dot(sb - sa, axis);
        var tb1 = Vec3d.Dot(eb - sa, axis);
        var lo = Math.Max(0, Math.Min(tb0, tb1));
        var hi = Math.Min(length, Math.Max(tb0, tb1));
        if (hi - lo <= PlaneEpsilon) return false;

        p = sa + axis * lo;
        q = sa + axis * hi;
        return true;
    }

    // Копланарные треугольники с сонаправленными нормалями и общей площадью: оба тела за одной плоскостью,
    // их внутренности перекрываются. Встречные нормали - касание. p и q - углы габарита общей части.
    private static bool Coplanar(
        in Vec3d a0, in Vec3d a1, in Vec3d a2,
        in Vec3d b0, in Vec3d b1, in Vec3d b2,
        in Vec3d nb, out Vec3d p, out Vec3d q)
    {
        p = default;
        q = default;
        var na = Vec3d.Cross(a1 - a0, a2 - a0);
        if (na.LengthSquared <= DegenerateAreaSquared || Vec3d.Dot(na, nb) <= 0) return false;

        // Отсечение треугольника A полуплоскостями рёбер B (Сазерленд - Ходжман), не больше 6 вершин.
        Span<Vec3d> polygon = stackalloc Vec3d[9];
        Span<Vec3d> next = stackalloc Vec3d[9];
        polygon[0] = a0;
        polygon[1] = a1;
        polygon[2] = a2;
        var count = 3;
        count = Clip(polygon, count, next, b0, Vec3d.Cross(nb, b1 - b0));
        count = Clip(next, count, polygon, b1, Vec3d.Cross(nb, b2 - b1));
        count = Clip(polygon, count, next, b2, Vec3d.Cross(nb, b0 - b2));
        if (count < 3) return false;

        var doubleArea = default(Vec3d);
        for (var i = 1; i + 1 < count; i++) doubleArea += Vec3d.Cross(next[i] - next[0], next[i + 1] - next[0]);
        if (Vec3d.Dot(doubleArea, nb) <= 2 * CoplanarAreaEpsilon) return false;

        p = next[0];
        q = next[0];
        for (var i = 1; i < count; i++)
        {
            p = new Vec3d(Math.Min(p.X, next[i].X), Math.Min(p.Y, next[i].Y), Math.Min(p.Z, next[i].Z));
            q = new Vec3d(Math.Max(q.X, next[i].X), Math.Max(q.Y, next[i].Y), Math.Max(q.Z, next[i].Z));
        }

        return true;
    }

    // Оставляет часть многоугольника, где Dot(inward, v - origin) >= 0.
    private static int Clip(Span<Vec3d> input, int count, Span<Vec3d> output, in Vec3d origin, in Vec3d inward)
    {
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var current = input[i];
            var following = input[(i + 1) % count];
            var dc = Vec3d.Dot(inward, current - origin);
            var df = Vec3d.Dot(inward, following - origin);
            if (dc >= 0) output[result++] = current;
            if ((dc >= 0) != (df >= 0)) output[result++] = current + (following - current) * (dc / (dc - df));
        }

        return result;
    }

    private static double Snap(double d) => Math.Abs(d) < PlaneEpsilon ? 0 : d;

    private static bool StrictlyOneSide(double d0, double d1, double d2) =>
        (d0 > 0 && d1 > 0 && d2 > 0) || (d0 < 0 && d1 < 0 && d2 < 0);

    private static bool Crosses(double d0, double d1, double d2) =>
        (d0 > 0 || d1 > 0 || d2 > 0) && (d0 < 0 || d1 < 0 || d2 < 0);

    // Ребро на плоскости, третья вершина за ней против нормали.
    private static bool RestsBehind(double d0, double d1, double d2)
    {
        var zeros = (d0 == 0 ? 1 : 0) + (d1 == 0 ? 1 : 0) + (d2 == 0 ? 1 : 0);
        return zeros == 2 && d0 + d1 + d2 < 0;
    }

    // Отрезок треугольника на плоскости другого: вершины на плоскости и точки пересечения рёбер со сменой знака.
    // Вызывается, когда треугольник пересекает плоскость или лежит на ней ребром, поэтому точек ровно две.
    private static void PlaneSegment(
        in Vec3d v0, in Vec3d v1, in Vec3d v2, double d0, double d1, double d2, out Vec3d s, out Vec3d e)
    {
        Span<Vec3d> points = stackalloc Vec3d[2];
        var count = 0;
        Add(v0, d0, v1, d1, points, ref count);
        Add(v1, d1, v2, d2, points, ref count);
        Add(v2, d2, v0, d0, points, ref count);
        s = points[0];
        e = count > 1 ? points[1] : points[0];
    }

    private static void Add(in Vec3d v, double dv, in Vec3d w, double dw, Span<Vec3d> points, ref int count)
    {
        if (count == 2) return;
        if (dv == 0)
        {
            points[count++] = v;
            return;
        }

        if ((dv > 0 && dw < 0) || (dv < 0 && dw > 0)) points[count++] = v + (w - v) * (dv / (dv - dw));
    }
}
