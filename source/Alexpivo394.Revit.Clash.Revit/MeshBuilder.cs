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
        _positions.Add((float)(point.X - origin.X));
        _positions.Add((float)(point.Y - origin.Y));
        _positions.Add((float)(point.Z - origin.Z));
        return index;
    }
}
