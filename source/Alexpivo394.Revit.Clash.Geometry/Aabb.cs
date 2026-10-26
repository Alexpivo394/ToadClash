using System.Numerics;

namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Ось-выровненный ограничивающий параллелепипед, мм.</summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    /// <summary>Пустой бокс: объединение с ним возвращает второй бокс.</summary>
    public static Aabb Empty { get; } = new(
        new Vector3(float.PositiveInfinity),
        new Vector3(float.NegativeInfinity));

    /// <summary>Центр бокса.</summary>
    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Пересекаются ли боксы. Касание по грани считается пересечением.</summary>
    public bool Overlaps(in Aabb other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    /// <summary>Наименьший бокс, содержащий оба.</summary>
    public Aabb Union(in Aabb other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>Бокс, увеличенный на <paramref name="by"/> с каждой стороны.</summary>
    public Aabb Expand(float by)
    {
        var delta = new Vector3(by);
        return new Aabb(Min - delta, Max + delta);
    }

    /// <summary>Бокс по тройкам xyz. Для пустого списка возвращает <see cref="Empty"/>.</summary>
    public static Aabb FromPositions(ReadOnlySpan<float> xyz)
    {
        var min = Empty.Min;
        var max = Empty.Max;
        for (var i = 0; i + 2 < xyz.Length; i += 3)
        {
            var p = new Vector3(xyz[i], xyz[i + 1], xyz[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new Aabb(min, max);
    }
}
