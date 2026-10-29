using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Элемент сцены: сетка и габариты пересчитаны в систему координат сцены.</summary>
internal sealed class SceneElement
{
    public required ElementRef Ref { get; init; }

    /// <summary>Индекс раздела в <see cref="ClashScene.Disciplines"/>.</summary>
    public required int Discipline { get; init; }

    public required Guid[] Sets { get; init; }

    /// <summary>Габариты в системе координат сцены.</summary>
    public required Aabb Bounds { get; init; }

    /// <summary>Позиции вершин в системе координат сцены, тройки xyz.</summary>
    public required float[] Positions { get; init; }

    public required int[] Indices { get; init; }

    public required int ModelIndex { get; init; }

    /// <summary>Номер изделия в модели: элементы с общим корнем вложенных семейств друг с другом не проверяются.</summary>
    public required int Product { get; init; }
}

/// <summary>
///     Все модели части в одной системе: начало - Origin первой модели, позиции пересчитываются в float относительно него.
///     В сцену попадают только элементы с непустым членством в наборах и хотя бы одним невырожденным треугольником.
/// </summary>
public sealed class ClashScene
{
    /// <summary>Порог вырожденности: квадрат длины векторного произведения сторон треугольника.</summary>
    private const double DegenerateAreaSquared = 1e-12;

    private readonly HashSet<(int, int)> _connected;

    private ClashScene(
        List<SceneElement> elements,
        List<Guid> disciplines,
        HashSet<(int, int)> connected,
        double originX,
        double originY,
        double originZ,
        long triangleCount)
    {
        Elements = elements;
        Disciplines = disciplines;
        _connected = connected;
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        TriangleCount = triangleCount;
    }

    internal IReadOnlyList<SceneElement> Elements { get; }

    internal IReadOnlyList<Guid> Disciplines { get; }

    /// <summary>Начало системы координат сцены, мм.</summary>
    public double OriginX { get; }

    /// <summary>Начало системы координат сцены, мм.</summary>
    public double OriginY { get; }

    /// <summary>Начало системы координат сцены, мм.</summary>
    public double OriginZ { get; }

    /// <summary>Число элементов в сцене.</summary>
    public int ElementCount => Elements.Count;

    /// <summary>Число треугольников элементов сцены.</summary>
    public long TriangleCount { get; }

    /// <summary>Собирает сцену из моделей; начало координат - Origin первой модели.</summary>
    public static ClashScene Build(IReadOnlyList<ModelInput> models, SetMembership membership)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (membership is null) throw new ArgumentNullException(nameof(membership));

        var header0 = models.Count > 0 ? models[0].Geometry.Header : null;
        var ox = header0?.OriginX ?? 0;
        var oy = header0?.OriginY ?? 0;
        var oz = header0?.OriginZ ?? 0;

        var elements = new List<SceneElement>();
        var disciplines = new List<Guid>();
        var disciplineIndex = new Dictionary<Guid, int>();
        var connected = new HashSet<(int, int)>();
        long triangles = 0;

        for (var m = 0; m < models.Count; m++)
        {
            var model = models[m];
            var header = model.Geometry.Header;
            var same = header.OriginX == ox && header.OriginY == oy && header.OriginZ == oz;
            var byUniqueId = new Dictionary<string, int>(StringComparer.Ordinal);
            var products = new Dictionary<string, int>(StringComparer.Ordinal);
            var links = new List<(int Index, IReadOnlyList<string> Ids)>();

            foreach (var element in model.Geometry.Elements)
            {
                var info = element.Info;
                var sets = membership.SetsOf(header.ModelKey, info.UniqueId);
                if (sets.Count == 0) continue;
                var group = membership.GroupsOf(header.ModelKey, info.UniqueId)[0];
                if (!disciplineIndex.TryGetValue(group, out var discipline))
                {
                    discipline = disciplines.Count;
                    disciplines.Add(group);
                    disciplineIndex[group] = discipline;
                }

                var mesh = element.Mesh;
                if (!HasNonDegenerateTriangle(mesh)) continue;

                var positions = same ? mesh.Positions : Rebase(mesh.Positions, header, ox, oy, oz);
                var index = elements.Count;
                var root = info.RootComponentUniqueId ?? info.UniqueId;
                if (!products.TryGetValue(root, out var product)) products.Add(root, product = products.Count);
                elements.Add(new SceneElement
                {
                    Ref = new ElementRef(header.ModelKey, info.UniqueId),
                    Discipline = discipline,
                    Sets = [.. sets],
                    Bounds = Aabb.FromPositions(positions),
                    Positions = positions,
                    Indices = mesh.Indices,
                    ModelIndex = m,
                    Product = product
                });
                triangles += mesh.TriangleCount;
                byUniqueId[info.UniqueId] = index;
                if (info.ConnectedUniqueIds.Count > 0) links.Add((index, info.ConnectedUniqueIds));
            }

            foreach (var (index, ids) in links)
            {
                foreach (var id in ids)
                {
                    if (byUniqueId.TryGetValue(id, out var other) && other != index)
                        connected.Add(Normalize(index, other));
                }
            }
        }

        return new ClashScene(elements, disciplines, connected, ox, oy, oz, triangles);
    }

    /// <summary>Соединены ли элементы: одна модель и ссылка ConnectedUniqueIds в любую сторону.</summary>
    internal bool AreConnected(int a, int b) => _connected.Contains(Normalize(a, b));

    private static (int, int) Normalize(int a, int b) => a < b ? (a, b) : (b, a);

    private static float[] Rebase(float[] positions, ModelHeader header, double ox, double oy, double oz)
    {
        // absolute = modelOrigin + p; scene = absolute - sceneOrigin.
        var result = new float[positions.Length];
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            result[i] = (float)(header.OriginX + positions[i] - ox);
            result[i + 1] = (float)(header.OriginY + positions[i + 1] - oy);
            result[i + 2] = (float)(header.OriginZ + positions[i + 2] - oz);
        }

        return result;
    }

    private static bool HasNonDegenerateTriangle(ElementMesh mesh)
    {
        var p = mesh.Positions;
        var idx = mesh.Indices;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var a = idx[t] * 3;
            var b = idx[t + 1] * 3;
            var c = idx[t + 2] * 3;
            double e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
            double e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
            var cx = e1y * e2z - e1z * e2y;
            var cy = e1z * e2x - e1x * e2z;
            var cz = e1x * e2y - e1y * e2x;
            if (cx * cx + cy * cy + cz * cz > DegenerateAreaSquared) return true;
        }

        return false;
    }
}
