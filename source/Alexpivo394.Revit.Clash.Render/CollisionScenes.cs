using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>Сцена картинки: сетки относительно точки коллизии, половина стороны секущего куба и сколько окружения взято.</summary>
public sealed record RenderScene(IReadOnlyList<RenderMesh> Meshes, double HalfSizeMm, long ContextTriangles, bool Truncated);

/// <summary>Модель для сцены: оглавление слепка и способ открыть его model.tcg.</summary>
/// <param name="Index">Оглавление слепка.</param>
/// <param name="OpenGeometry">Открывает model.tcg этой модели на чтение с позиционированием; поток закрывает вызывающий <see cref="CollisionScenes.Build" />.</param>
public sealed record SceneModel(ModelIndex Index, Func<Stream> OpenGeometry);

/// <summary>Коллизия, для которой собирается сцена: элементы A и B и точка в общей системе, мм.</summary>
public sealed record SceneTarget(string ModelA, string UniqueIdA, string ModelB, string UniqueIdB, double X, double Y, double Z);

/// <summary>
///     Сборка сцены картинки по оглавлениям слепков: выбор по габаритам, с диска читаются только сетки выбранных элементов.
/// </summary>
public static class CollisionScenes
{
    /// <summary>Бюджет треугольников окружения по умолчанию.</summary>
    public const long DefaultContextTriangleBudget = 300_000;

    /// <summary>Число треугольников элемента из слепка до версии 3, где оно не записано.</summary>
    private const int UnknownTriangles = 1000;

    private readonly record struct Candidate(int Model, int Element, double Distance, int Triangles);

    /// <summary>
    ///     Собирает сцену коллизии: A, B и окружение — элементы, чей габарит пересекает секущий куб, ближние первыми.
    ///     Элемент, не помещающийся в бюджет треугольников, пропускается, остальные берутся дальше.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Элемента A или B нет в оглавлениях.</exception>
    public static RenderScene Build(IReadOnlyList<SceneModel> models, SceneTarget target, long contextTriangleBudget = DefaultContextTriangleBudget)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(target);
        var (x, y, z) = (target.X, target.Y, target.Z);

        var a = Find(models, target.ModelA, target.UniqueIdA);
        var b = Find(models, target.ModelB, target.UniqueIdB);
        var streams = new Stream?[models.Count];
        try
        {
            return Build(models, streams, a, b, x, y, z, contextTriangleBudget);
        }
        finally
        {
            foreach (var stream in streams) stream?.Dispose();
        }
    }

    private static RenderScene Build(IReadOnlyList<SceneModel> models, Stream?[] streams, (int Model, int Element) a, (int Model, int Element) b,
        double x, double y, double z, long contextTriangleBudget)
    {
        var meshA = Read(models, streams, a.Model, a.Element, RenderRole.A, x, y, z);
        var meshB = Read(models, streams, b.Model, b.Element, RenderRole.B, x, y, z);
        // Куб — по зоне пересечения тел, а не габаритов: у длинных элементов под углом габариты пересекаются на метры.
        var zone = IntersectionZone.Measure(meshA.Positions, meshA.Indices, meshB.Positions, meshB.Indices);
        var half = zone == Aabb.Empty
            ? CollisionRenderer.HalfSize(RelativeBounds(models, a, x, y, z), RelativeBounds(models, b, x, y, z))
            : CollisionRenderer.HalfSize(zone);

        var inside = new List<Candidate>();
        for (var m = 0; m < models.Count; m++)
        {
            var header = models[m].Index.Header;
            var elements = models[m].Index.Elements;
            for (var e = 0; e < elements.Count; e++)
            {
                if ((m, e) == a || (m, e) == b) continue;
                var element = elements[e];
                var triangles = element.TriangleCount < 0 ? UnknownTriangles : element.TriangleCount;
                if (triangles == 0) continue;

                var bounds = element.Bounds;
                double minX = header.OriginX + bounds.Min.X, minY = header.OriginY + bounds.Min.Y, minZ = header.OriginZ + bounds.Min.Z;
                double maxX = header.OriginX + bounds.Max.X, maxY = header.OriginY + bounds.Max.Y, maxZ = header.OriginZ + bounds.Max.Z;
                if (!(minX <= maxX)) continue;
                if (maxX < x - half || minX > x + half || maxY < y - half || minY > y + half || maxZ < z - half || minZ > z + half) continue;

                var dx = Gap(x, minX, maxX);
                var dy = Gap(y, minY, maxY);
                var dz = Gap(z, minZ, maxZ);
                inside.Add(new Candidate(m, e, dx * dx + dy * dy + dz * dz, triangles));
            }
        }

        inside.Sort((l, r) => Compare(models, l, r));

        var meshes = new List<RenderMesh> { meshA, meshB };
        long total = 0;
        var truncated = false;
        foreach (var candidate in inside)
        {
            if (total + candidate.Triangles > contextTriangleBudget)
            {
                truncated = true;
                continue;
            }

            total += candidate.Triangles;
            meshes.Add(Read(models, streams, candidate.Model, candidate.Element, RenderRole.Context, x, y, z));
        }

        return new RenderScene(meshes, half, total, truncated);
    }

    private static int Compare(IReadOnlyList<SceneModel> models, Candidate l, Candidate r)
    {
        var byDistance = l.Distance.CompareTo(r.Distance);
        if (byDistance != 0) return byDistance;
        var byModel = string.CompareOrdinal(models[l.Model].Index.Header.ModelKey, models[r.Model].Index.Header.ModelKey);
        if (byModel != 0) return byModel;
        return string.CompareOrdinal(models[l.Model].Index.Elements[l.Element].Info.UniqueId, models[r.Model].Index.Elements[r.Element].Info.UniqueId);
    }

    private static double Gap(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;

    private static (int Model, int Element) Find(IReadOnlyList<SceneModel> models, string modelKey, string uniqueId)
    {
        for (var m = 0; m < models.Count; m++)
        {
            if (models[m].Index.Header.ModelKey != modelKey) continue;
            var elements = models[m].Index.Elements;
            for (var e = 0; e < elements.Count; e++)
                if (elements[e].Info.UniqueId == uniqueId)
                    return (m, e);
        }

        throw new KeyNotFoundException($"Элемента {uniqueId} нет в модели {modelKey}.");
    }

    private static Aabb RelativeBounds(IReadOnlyList<SceneModel> models, (int Model, int Element) id, double x, double y, double z)
    {
        var header = models[id.Model].Index.Header;
        var bounds = models[id.Model].Index.Elements[id.Element].Bounds;
        double minX = header.OriginX + bounds.Min.X, minY = header.OriginY + bounds.Min.Y, minZ = header.OriginZ + bounds.Min.Z;
        double maxX = header.OriginX + bounds.Max.X, maxY = header.OriginY + bounds.Max.Y, maxZ = header.OriginZ + bounds.Max.Z;
        if (!(minX <= maxX)) return Aabb.Empty;
        return new Aabb(
            new Vector3((float)(minX - x), (float)(minY - y), (float)(minZ - z)),
            new Vector3((float)(maxX - x), (float)(maxY - y), (float)(maxZ - z)));
    }

    /// <summary>Сетка элемента относительно точки: (float)(Origin + v − point), считается в double.</summary>
    private static RenderMesh Read(IReadOnlyList<SceneModel> models, Stream?[] streams, int model, int element, RenderRole role, double x, double y, double z)
    {
        var stream = streams[model] ??= models[model].OpenGeometry();
        var header = models[model].Index.Header;
        var mesh = ModelGeometryFile.ReadMesh(stream, models[model].Index.Elements[element]);
        double dx = header.OriginX - x, dy = header.OriginY - y, dz = header.OriginZ - z;
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
