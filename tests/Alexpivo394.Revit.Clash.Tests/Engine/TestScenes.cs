using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

/// <summary>Фабрики тестовых сеток, моделей и конфигураций для тестов движка.</summary>
public static class TestScenes
{
    /// <summary>Элемент тестовой модели.</summary>
    public sealed record Item(string UniqueId, string Category, ElementMesh Mesh, string[]? Connected = null, string? Root = null);

    /// <summary>Параллелепипед: 8 вершин, 12 треугольников.</summary>
    public static ElementMesh Box(Vector3 min, Vector3 max)
    {
        float[] p =
        [
            min.X, min.Y, min.Z, max.X, min.Y, min.Z, max.X, max.Y, min.Z, min.X, max.Y, min.Z,
            min.X, min.Y, max.Z, max.X, min.Y, max.Z, max.X, max.Y, max.Z, min.X, max.Y, max.Z
        ];
        int[] i =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7
        ];
        return new ElementMesh(p, i);
    }

    /// <summary>Цилиндр с осью от <paramref name="a"/> до <paramref name="b"/> и торцевыми крышками.</summary>
    public static ElementMesh Cylinder(Vector3 a, Vector3 b, float radius, int segments)
    {
        var axis = Vector3.Normalize(b - a);
        var helper = MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        var v = Vector3.Cross(axis, u);

        var positions = new List<float>();

        void Add(Vector3 q)
        {
            positions.Add(q.X);
            positions.Add(q.Y);
            positions.Add(q.Z);
        }

        for (var k = 0; k < segments; k++)
        {
            var angle = 2 * MathF.PI * k / segments;
            var offset = radius * (MathF.Cos(angle) * u + MathF.Sin(angle) * v);
            Add(a + offset);
            Add(b + offset);
        }

        var centerA = segments * 2;
        var centerB = centerA + 1;
        Add(a);
        Add(b);

        var indices = new List<int>();
        for (var k = 0; k < segments; k++)
        {
            var n = (k + 1) % segments;
            int a0 = k * 2, b0 = k * 2 + 1, a1 = n * 2, b1 = n * 2 + 1;
            indices.AddRange([a0, a1, b1, a0, b1, b0]);
            indices.AddRange([centerA, a1, a0]);
            indices.AddRange([centerB, b0, b1]);
        }

        return new ElementMesh([.. positions], [.. indices]);
    }

    /// <summary>Модель раздела с элементами; ключ модели и начало координат задаются явно.</summary>
    public static ModelInput Model(
        string key,
        string discipline,
        (double X, double Y, double Z) origin,
        IEnumerable<Item> items)
    {
        var header = new ModelHeader(key, "", 2024, DateTimeOffset.UnixEpoch, true, "", origin.X, origin.Y, origin.Z);
        var elements = items
            .Select(item => new ElementGeometry(
                new ElementInfo(item.UniqueId, 1, item.Category, null, null, null, null, item.Connected ?? [], item.Root),
                item.Mesh,
                Aabb.FromPositions(item.Mesh.Positions),
                0))
            .ToList();
        return new ModelInput(discipline, new ModelGeometry(header, elements, []),
            new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()));
    }

    /// <summary>Набор раздела, в который входят элементы заданной категории.</summary>
    public static SelectionSet CategorySet(string discipline, string category) =>
        new(Guid.NewGuid(), discipline, category, [
            new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, category)])]);
}
