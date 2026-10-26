namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Треугольная сетка элемента. Позиции в мм относительно <see cref="ModelHeader"/> Origin, тройки xyz; индексы - тройки треугольников.</summary>
public sealed class ElementMesh
{
    /// <summary>Создаёт сетку и проверяет кратность массивов трём и допустимость индексов.</summary>
    /// <exception cref="ArgumentException">Длина массива не кратна трём или индекс вне диапазона вершин.</exception>
    public ElementMesh(float[] positions, int[] indices)
    {
        if (positions is null) throw new ArgumentNullException(nameof(positions));
        if (indices is null) throw new ArgumentNullException(nameof(indices));
        if (positions.Length % 3 != 0)
            throw new ArgumentException("Длина массива позиций должна быть кратна 3.", nameof(positions));
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Длина массива индексов должна быть кратна 3.", nameof(indices));

        var vertexCount = positions.Length / 3;
        foreach (var index in indices)
        {
            if (index < 0 || index >= vertexCount)
                throw new ArgumentException($"Индекс вершины {index} вне диапазона [0, {vertexCount}).", nameof(indices));
        }

        Positions = positions;
        Indices = indices;
    }

    /// <summary>Координаты вершин, тройки xyz.</summary>
    public float[] Positions { get; }

    /// <summary>Индексы вершин, тройки треугольников.</summary>
    public int[] Indices { get; }

    /// <summary>Число треугольников.</summary>
    public int TriangleCount => Indices.Length / 3;
}
