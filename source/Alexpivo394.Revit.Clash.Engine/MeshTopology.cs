namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Замкнутость и внешняя ориентация сетки элемента. Вершины свариваются по точному совпадению координат.
///     Сетка замкнута, если каждое неориентированное ребро принадлежит чётному числу треугольников: поверхность
///     без края, и чётность пересечений луча определена. Так замкнут и элемент из нескольких тел, касающихся
///     по рёбрам (корпус и фланцы): ребро касания общее для четырёх треугольников.
/// </summary>
/// <remarks>
///     Замкнутая сетка ориентируется наружу по каждой оболочке отдельно: оболочка с отрицательным объёмом
///     переворачивается. Оболочка - треугольники, связанные рёбрами ровно двух треугольников, поэтому тела,
///     касающиеся по ребру или в вершине, - разные оболочки. Внутренняя полость, смоделированная отдельной
///     оболочкой, тоже развернётся наружу. Незамкнутые сетки сохраняют исходный порядок обхода.
/// </remarks>
internal static class MeshTopology
{
    /// <summary>
    ///     Возвращает замкнутость сетки и индексы с внешней ориентацией; входной массив не меняется,
    ///     при перевороте возвращается копия.
    /// </summary>
    public static int[] Orient(float[] positions, int[] indices, out bool closed) => Orient(positions, indices, out closed, out _);

    /// <summary>
    ///     То же и номер оболочки каждого треугольника (0, 1, …) у замкнутой сетки; у незамкнутой - null. Каждая
    ///     оболочка - отдельное замкнутое тело, тела элемента могут касаться и перекрываться.
    /// </summary>
    public static int[] Orient(float[] positions, int[] indices, out bool closed, out int[]? shells)
    {
        closed = false;
        shells = null;
        if (indices.Length == 0) return indices;

        var ids = Weld(positions, out _);
        var edges = CountEdges(ids, indices);
        if (edges.Count == 0) return indices;
        foreach (var edge in edges.Values)
        {
            if (edge.Count % 2 != 0) return indices;
        }

        closed = true;

        // Оболочки: треугольники, соединённые рёбрами ровно двух треугольников.
        var triangleCount = indices.Length / 3;
        var parent = new int[triangleCount];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        foreach (var edge in edges.Values)
        {
            if (edge.Count == 2) Union(parent, edge.First, edge.Second);
        }

        // Объём каждой оболочки относительно её первой вершины - для точности вдали от начала координат.
        var volume = new double[triangleCount];
        var reference = new int[triangleCount];
        Array.Fill(reference, -1);
        for (var t = 0; t < triangleCount; t++)
        {
            var root = Find(parent, t);
            if (reference[root] < 0) reference[root] = indices[t * 3];
            var r = Vec3d.At(positions, reference[root]);
            var a = Vec3d.At(positions, indices[t * 3]) - r;
            var b = Vec3d.At(positions, indices[t * 3 + 1]) - r;
            var c = Vec3d.At(positions, indices[t * 3 + 2]) - r;
            volume[root] += Vec3d.Dot(a, Vec3d.Cross(b, c));
        }

        var numbers = new Dictionary<int, int>();
        shells = new int[triangleCount];
        for (var t = 0; t < triangleCount; t++)
        {
            var root = Find(parent, t);
            if (!numbers.TryGetValue(root, out var number)) numbers[root] = number = numbers.Count;
            shells[t] = number;
        }

        int[]? oriented = null;
        for (var t = 0; t < triangleCount; t++)
        {
            if (volume[Find(parent, t)] >= 0) continue;
            oriented ??= (int[])indices.Clone();
            (oriented[t * 3 + 1], oriented[t * 3 + 2]) = (oriented[t * 3 + 2], oriented[t * 3 + 1]);
        }

        return oriented ?? indices;
    }

    private static int[] Weld(float[] positions, out int count)
    {
        var welded = new Dictionary<(float, float, float), int>();
        var ids = new int[positions.Length / 3];
        for (var v = 0; v < ids.Length; v++)
        {
            var key = (positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            if (!welded.TryGetValue(key, out var id))
            {
                id = welded.Count;
                welded[key] = id;
            }

            ids[v] = id;
        }

        count = welded.Count;
        return ids;
    }

    // Неориентированные рёбра по сваренным вершинам: число треугольников и первые два из них.
    // Треугольники, выродившиеся после сварки, пропускаются.
    private static Dictionary<(int, int), EdgeUse> CountEdges(int[] ids, int[] indices)
    {
        var edges = new Dictionary<(int, int), EdgeUse>();
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int x = ids[indices[t]], y = ids[indices[t + 1]], z = ids[indices[t + 2]];
            if (x == y || y == z || z == x) continue;
            Count(edges, x, y, t / 3);
            Count(edges, y, z, t / 3);
            Count(edges, z, x, t / 3);
        }

        return edges;
    }

    private static void Count(Dictionary<(int, int), EdgeUse> edges, int x, int y, int triangle)
    {
        var key = x < y ? (x, y) : (y, x);
        edges[key] = edges.TryGetValue(key, out var use)
            ? use with { Count = use.Count + 1, Second = use.Count == 1 ? triangle : use.Second }
            : new EdgeUse(1, triangle, -1);
    }

    private readonly record struct EdgeUse(int Count, int First, int Second);

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    private static void Union(int[] parent, int x, int y)
    {
        x = Find(parent, x);
        y = Find(parent, y);
        if (x != y) parent[y] = x;
    }
}
