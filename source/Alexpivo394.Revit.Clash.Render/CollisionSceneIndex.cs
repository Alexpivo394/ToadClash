using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>Сцена картинки: сетки относительно точки коллизии, половина стороны секущего куба и сколько окружения взято.</summary>
public sealed record RenderScene(IReadOnlyList<RenderMesh> Meshes, double HalfSizeMm, long ContextTriangles, bool Truncated);

/// <summary>
///     Индекс элементов загруженных моделей для сцен картинок: равномерная сетка по габаритам в общей системе, ячейка 2 м.
///     Строится один раз, потокобезопасен на чтение.
/// </summary>
public sealed class CollisionSceneIndex
{
    private const double CellMm = 2000;

    /// <summary>Элемент, занимающий больше ячеек, хранится в общем списке и проверяется всегда.</summary>
    private const long MaxCellsPerElement = 4096;

    private readonly IReadOnlyList<ModelGeometry> _models;
    private readonly Dictionary<(string Model, string UniqueId), int> _byId = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<(long X, long Y, long Z), List<int>> _cells = new();
    private readonly List<int> _large = [];

    private readonly record struct Entry(int Model, int Element, double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ, int Triangles);

    /// <summary>Строит индекс по моделям части.</summary>
    public CollisionSceneIndex(IReadOnlyList<ModelGeometry> models)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        for (var m = 0; m < models.Count; m++)
        {
            var header = models[m].Header;
            var elements = models[m].Elements;
            for (var e = 0; e < elements.Count; e++)
            {
                var element = elements[e];
                var bounds = element.Bounds;
                var entry = new Entry(m, e,
                    header.OriginX + bounds.Min.X, header.OriginY + bounds.Min.Y, header.OriginZ + bounds.Min.Z,
                    header.OriginX + bounds.Max.X, header.OriginY + bounds.Max.Y, header.OriginZ + bounds.Max.Z,
                    element.Mesh.TriangleCount);
                var id = _entries.Count;
                _entries.Add(entry);
                _byId[(header.ModelKey, element.Info.UniqueId)] = id;
                if (entry.Triangles == 0 || !(entry.MinX <= entry.MaxX)) continue;

                var (x0, y0, z0) = Cell(entry.MinX, entry.MinY, entry.MinZ);
                var (x1, y1, z1) = Cell(entry.MaxX, entry.MaxY, entry.MaxZ);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > MaxCellsPerElement)
                {
                    _large.Add(id);
                    continue;
                }

                for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                for (var z = z0; z <= z1; z++)
                {
                    if (!_cells.TryGetValue((x, y, z), out var list)) _cells[(x, y, z)] = list = [];
                    list.Add(id);
                }
            }
        }
    }

    private static (long X, long Y, long Z) Cell(double x, double y, double z) =>
        ((long)Math.Floor(x / CellMm), (long)Math.Floor(y / CellMm), (long)Math.Floor(z / CellMm));

    /// <summary>
    ///     Собирает сцену коллизии: A, B и окружение — элементы, чей габарит пересекает секущий куб, ближние первыми,
    ///     пока помещаются в бюджет треугольников. Точка — общая система, мм.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Элемента A или B нет в моделях.</exception>
    public RenderScene Collect(string modelA, string uniqueIdA, string modelB, string uniqueIdB, double x, double y, double z,
        long contextTriangleBudget = 300_000)
    {
        var a = Find(modelA, uniqueIdA);
        var b = Find(modelB, uniqueIdB);
        var meshA = Relative(a, RenderRole.A, x, y, z);
        var meshB = Relative(b, RenderRole.B, x, y, z);
        var half = CollisionRenderer.HalfSize(RelativeBounds(a, x, y, z), RelativeBounds(b, x, y, z));

        var candidates = new HashSet<int>();
        var (cx0, cy0, cz0) = Cell(x - half, y - half, z - half);
        var (cx1, cy1, cz1) = Cell(x + half, y + half, z + half);
        for (var cx = cx0; cx <= cx1; cx++)
        for (var cy = cy0; cy <= cy1; cy++)
        for (var cz = cz0; cz <= cz1; cz++)
            if (_cells.TryGetValue((cx, cy, cz), out var list))
                candidates.UnionWith(list);
        candidates.UnionWith(_large);
        candidates.Remove(a);
        candidates.Remove(b);

        var inside = new List<(double Distance, int Id)>();
        foreach (var id in candidates)
        {
            var e = _entries[id];
            if (e.MaxX < x - half || e.MinX > x + half || e.MaxY < y - half || e.MinY > y + half || e.MaxZ < z - half || e.MinZ > z + half)
                continue;
            var dx = Gap(x, e.MinX, e.MaxX);
            var dy = Gap(y, e.MinY, e.MaxY);
            var dz = Gap(z, e.MinZ, e.MaxZ);
            inside.Add((dx * dx + dy * dy + dz * dz, id));
        }

        inside.Sort((l, r) => l.Distance != r.Distance ? l.Distance.CompareTo(r.Distance) : l.Id.CompareTo(r.Id));

        var meshes = new List<RenderMesh> { meshA, meshB };
        long triangles = 0;
        var truncated = false;
        foreach (var (_, id) in inside)
        {
            var count = _entries[id].Triangles;
            if (triangles + count > contextTriangleBudget)
            {
                truncated = true;
                break;
            }

            triangles += count;
            meshes.Add(Relative(id, RenderRole.Context, x, y, z));
        }

        return new RenderScene(meshes, half, triangles, truncated);
    }

    private static double Gap(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;

    private int Find(string model, string uniqueId) =>
        _byId.TryGetValue((model, uniqueId), out var id)
            ? id
            : throw new KeyNotFoundException($"Элемента {uniqueId} нет в модели {model}.");

    private Aabb RelativeBounds(int id, double x, double y, double z)
    {
        var e = _entries[id];
        if (!(e.MinX <= e.MaxX)) return Aabb.Empty;
        return new Aabb(
            new Vector3((float)(e.MinX - x), (float)(e.MinY - y), (float)(e.MinZ - z)),
            new Vector3((float)(e.MaxX - x), (float)(e.MaxY - y), (float)(e.MaxZ - z)));
    }

    /// <summary>Сетка элемента относительно точки: (float)(Origin + v − point), считается в double.</summary>
    private RenderMesh Relative(int id, RenderRole role, double x, double y, double z)
    {
        var entry = _entries[id];
        var model = _models[entry.Model];
        var mesh = model.Elements[entry.Element].Mesh;
        double dx = model.Header.OriginX - x, dy = model.Header.OriginY - y, dz = model.Header.OriginZ - z;
        var source = mesh.Positions;
        var positions = new float[source.Length];
        for (var i = 0; i + 2 < source.Length; i += 3)
        {
            positions[i] = (float)(dx + source[i]);
            positions[i + 1] = (float)(dy + source[i + 1]);
            positions[i + 2] = (float)(dz + source[i + 2]);
        }

        return new RenderMesh(role, positions, mesh.Indices);
    }
}
