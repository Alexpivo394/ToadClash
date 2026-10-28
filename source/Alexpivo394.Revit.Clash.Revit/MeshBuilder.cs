using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>Точка или вектор в мм, двойная точность. Без типов Revit, чтобы сшивку можно было проверять без Revit.</summary>
internal readonly record struct Vector3d(double X, double Y, double Z);

/// <summary>
///     Собирает сетку элемента из треугольников в абсолютных мм. Вершины сшиваются по решётке с шагом 0.01 мм от
///     <paramref name="origin"/>; треугольник, у которого после сшивки нулевая площадь, отбрасывается.
///     Порядок вершин треугольника сохраняется.
/// </summary>
internal sealed class MeshBuilder(Vector3d origin)
{
    private const double WeldStepMm = 0.01;

    private readonly Dictionary<(long X, long Y, long Z), int> _vertexByCell = new();
    private readonly List<float> _positions = [];
    private readonly List<int> _indices = [];
    private readonly List<(long X, long Y, long Z)> _cells = [];

    /// <summary>Отметка начала тела: число индексов и вершин на момент вызова.</summary>
    public readonly record struct ShellMark(int IndexCount, int VertexCount);

    /// <summary>Отмечает начало тела, чтобы проверить его замкнутость или откатить.</summary>
    public ShellMark Mark() => new(_indices.Count, _positions.Count / 3);

    /// <summary>Замкнуты ли треугольники, добавленные после отметки: каждое ребро после сшивки ровно в двух.</summary>
    public bool IsClosedSince(ShellMark mark)
    {
        if (_indices.Count == mark.IndexCount) return false;
        var edges = new Dictionary<(int, int), int>();
        for (var t = mark.IndexCount; t < _indices.Count; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var a = _indices[t + k];
                var b = _indices[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }

        return edges.Values.All(count => count == 2);
    }

    /// <summary>Удаляет треугольники и вершины, добавленные после отметки.</summary>
    public void Rollback(ShellMark mark)
    {
        _indices.RemoveRange(mark.IndexCount, _indices.Count - mark.IndexCount);
        for (var v = mark.VertexCount; v < _cells.Count; v++) _vertexByCell.Remove(_cells[v]);
        _cells.RemoveRange(mark.VertexCount, _cells.Count - mark.VertexCount);
        _positions.RemoveRange(mark.VertexCount * 3, _positions.Count - mark.VertexCount * 3);
    }

    public void AddTriangle(Vector3d a, Vector3d b, Vector3d c)
    {
        var cellA = Cell(a);
        var cellB = Cell(b);
        var cellC = Cell(c);
        if (HasZeroArea(cellA, cellB, cellC)) return;

        _indices.Add(VertexIndex(cellA, a));
        _indices.Add(VertexIndex(cellB, b));
        _indices.Add(VertexIndex(cellC, c));
    }

    public ElementMesh Build() => new(_positions.ToArray(), _indices.ToArray());

    private (long X, long Y, long Z) Cell(Vector3d point) => (
        (long)Math.Round((point.X - origin.X) / WeldStepMm),
        (long)Math.Round((point.Y - origin.Y) / WeldStepMm),
        (long)Math.Round((point.Z - origin.Z) / WeldStepMm));

    /// <summary>Площадь на решётке сшивки: совпавшие или коллинеарные после сшивки вершины дают ровно ноль.</summary>
    private static bool HasZeroArea((long X, long Y, long Z) a, (long X, long Y, long Z) b, (long X, long Y, long Z) c)
    {
        double abX = b.X - a.X, abY = b.Y - a.Y, abZ = b.Z - a.Z;
        double acX = c.X - a.X, acY = c.Y - a.Y, acZ = c.Z - a.Z;
        return abY * acZ - abZ * acY == 0 &&
               abZ * acX - abX * acZ == 0 &&
               abX * acY - abY * acX == 0;
    }

    private int VertexIndex((long X, long Y, long Z) cell, Vector3d point)
    {
        if (_vertexByCell.TryGetValue(cell, out var index)) return index;

        index = _positions.Count / 3;
        _vertexByCell.Add(cell, index);
        _cells.Add(cell);
        _positions.Add((float)(point.X - origin.X));
        _positions.Add((float)(point.Y - origin.Y));
        _positions.Add((float)(point.Z - origin.Z));
        return index;
    }
}
