namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Замкнутость и внешняя ориентация сетки элемента. Вершины свариваются по точному совпадению координат.
///     Сетка замкнута, если каждое неориентированное ребро принадлежит ровно двум треугольникам.
/// </summary>
/// <remarks>
///     Замкнутая сетка ориентируется наружу по каждой связной оболочке отдельно: оболочка с отрицательным
///     объёмом переворачивается. Внутренняя полость, смоделированная отдельной оболочкой, поэтому тоже
///     развернётся наружу. Незамкнутые сетки сохраняют исходный порядок обхода.
/// </remarks>
internal static class MeshTopology
{
    /// <summary>
    ///     Возвращает замкнутость сетки и индексы с внешней ориентацией; входной массив не меняется,
    ///     при перевороте возвращается копия.
    /// </summary>
    public static int[] Orient(float[] positions, int[] indices, out bool closed)
    {
        closed = false;
        if (indices.Length == 0) return indices;

        var ids = Weld(positions, out var weldedCount);
        if (!IsClosed(ids, indices)) return indices;
        closed = true;

        // Связные оболочки по сваренным вершинам.
        var parent = new int[weldedCount];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            Union(parent, ids[indices[t]], ids[indices[t + 1]]);
            Union(parent, ids[indices[t]], ids[indices[t + 2]]);
        }

        // Объём каждой оболочки относительно её первой вершины - для точности вдали от начала координат.
        var volume = new double[weldedCount];
        var reference = new int[weldedCount];
        Array.Fill(reference, -1);
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var root = Find(parent, ids[indices[t]]);
            if (reference[root] < 0) reference[root] = indices[t];
            var r = Vec3d.At(positions, reference[root]);
            var a = Vec3d.At(positions, indices[t]) - r;
            var b = Vec3d.At(positions, indices[t + 1]) - r;
            var c = Vec3d.At(positions, indices[t + 2]) - r;
            volume[root] += Vec3d.Dot(a, Vec3d.Cross(b, c));
        }

        int[]? oriented = null;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (volume[Find(parent, ids[indices[t]])] >= 0) continue;
            oriented ??= (int[])indices.Clone();
            (oriented[t + 1], oriented[t + 2]) = (oriented[t + 2], oriented[t + 1]);
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

    private static bool IsClosed(int[] ids, int[] indices)
    {
        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int x = ids[indices[t]], y = ids[indices[t + 1]], z = ids[indices[t + 2]];
            if (x == y || y == z || z == x) continue;
            Count(edges, x, y);
            Count(edges, y, z);
            Count(edges, z, x);
        }

        if (edges.Count == 0) return false;
        foreach (var count in edges.Values)
        {
            if (count != 2) return false;
        }

        return true;
    }

    private static void Count(Dictionary<(int, int), int> edges, int x, int y)
    {
        var key = x < y ? (x, y) : (y, x);
        edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
    }

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
