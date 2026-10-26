using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace ToadClash.Bench;

/// <summary>Примитивы сеток для синтетической части: параллелепипед, протяжка профиля по пути, отвод, тройник.</summary>
internal static class MeshPrimitives
{
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

    /// <summary>Выпуклый профиль круга против часовой стрелки в осях (u, v).</summary>
    public static Vector2[] Circle(int segments, float radius)
    {
        var profile = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = 2 * MathF.PI * i / segments;
            profile[i] = new Vector2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
        }

        return profile;
    }

    /// <summary>Профиль прямоугольника: ширина по u, высота по v.</summary>
    public static Vector2[] Rectangle(float width, float height)
    {
        float a = width / 2, b = height / 2;
        return [new(a, -b), new(a, b), new(-a, b), new(-a, -b)];
    }

    /// <summary>Прямой участок от <paramref name="a"/> до <paramref name="b"/>; <paramref name="rings"/> - число звеньев по длине.</summary>
    public static ElementMesh Straight(Vector3 a, Vector3 b, Vector2[] profile, int rings = 1)
    {
        var tangent = Vector3.Normalize(b - a);
        var u = SideOf(tangent);
        var v = Vector3.Cross(tangent, u);
        var centers = new Vector3[rings + 1];
        var us = new Vector3[rings + 1];
        var vs = new Vector3[rings + 1];
        for (var k = 0; k <= rings; k++)
        {
            centers[k] = Vector3.Lerp(a, b, (float)k / rings);
            us[k] = u;
            vs[k] = v;
        }

        return Sweep(centers, us, vs, profile);
    }

    /// <summary>
    ///     Отвод на 90 градусов: от <c>corner - d1 * radius</c> по дуге к <c>corner + d2 * radius</c>.
    ///     <paramref name="steps"/> - число звеньев дуги.
    /// </summary>
    public static ElementMesh Elbow(Vector3 corner, Vector3 d1, Vector3 d2, float radius, Vector2[] profile, int steps)
    {
        var center = corner - d1 * radius + d2 * radius;
        var normal = Vector3.Normalize(Vector3.Cross(d1, d2));
        var horizontalBend = MathF.Abs(normal.Z) > 0.9f;
        var centers = new Vector3[steps + 1];
        var us = new Vector3[steps + 1];
        var vs = new Vector3[steps + 1];
        for (var k = 0; k <= steps; k++)
        {
            var angle = MathF.PI / 2 * k / steps;
            var (sin, cos) = MathF.SinCos(angle);
            centers[k] = center + radius * (-d2 * cos + d1 * sin);
            var tangent = Vector3.Normalize(d2 * sin + d1 * cos);
            var u = horizontalBend ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, tangent)) : normal;
            us[k] = u;
            vs[k] = Vector3.Cross(tangent, u);
        }

        return Sweep(centers, us, vs, profile);
    }

    /// <summary>Объединяет сетки в одну (части могут пересекаться, как в реальных фитингах).</summary>
    public static ElementMesh Merge(params ElementMesh[] meshes)
    {
        var vertexCount = 0;
        var indexCount = 0;
        foreach (var mesh in meshes)
        {
            vertexCount += mesh.Positions.Length;
            indexCount += mesh.Indices.Length;
        }

        var positions = new float[vertexCount];
        var indices = new int[indexCount];
        int p = 0, ix = 0;
        foreach (var mesh in meshes)
        {
            var offset = p / 3;
            mesh.Positions.CopyTo(positions, p);
            for (var i = 0; i < mesh.Indices.Length; i++) indices[ix + i] = mesh.Indices[i] + offset;
            p += mesh.Positions.Length;
            ix += mesh.Indices.Length;
        }

        return new ElementMesh(positions, indices);
    }

    /// <summary>Горизонтальная поперечная ось профиля; для вертикального участка - X.</summary>
    private static Vector3 SideOf(Vector3 tangent) =>
        MathF.Abs(tangent.Z) > 0.99f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, tangent));

    /// <summary>
    ///     Протяжка выпуклого профиля по кольцам с торцевыми крышками. Оси колец правые: u x v = касательная.
    ///     Треугольников: 2 * m * (колец - 1) + 2 * m, где m - число точек профиля.
    /// </summary>
    private static ElementMesh Sweep(Vector3[] centers, Vector3[] us, Vector3[] vs, Vector2[] profile)
    {
        var m = profile.Length;
        var rings = centers.Length;
        var positions = new float[(rings * m + 2) * 3];
        for (var k = 0; k < rings; k++)
        {
            for (var i = 0; i < m; i++)
            {
                var q = centers[k] + us[k] * profile[i].X + vs[k] * profile[i].Y;
                var at = (k * m + i) * 3;
                positions[at] = q.X;
                positions[at + 1] = q.Y;
                positions[at + 2] = q.Z;
            }
        }

        var firstCenter = rings * m;
        var lastCenter = firstCenter + 1;
        Write(positions, firstCenter, centers[0]);
        Write(positions, lastCenter, centers[^1]);

        var indices = new int[(2 * m * (rings - 1) + 2 * m) * 3];
        var t = 0;
        for (var k = 0; k < rings - 1; k++)
        {
            for (var i = 0; i < m; i++)
            {
                var n = (i + 1) % m;
                int a = k * m + i, b = k * m + n, c = (k + 1) * m + n, d = (k + 1) * m + i;
                indices[t++] = a;
                indices[t++] = b;
                indices[t++] = c;
                indices[t++] = a;
                indices[t++] = c;
                indices[t++] = d;
            }
        }

        var lastRing = (rings - 1) * m;
        for (var i = 0; i < m; i++)
        {
            var n = (i + 1) % m;
            indices[t++] = firstCenter;
            indices[t++] = n;
            indices[t++] = i;
            indices[t++] = lastCenter;
            indices[t++] = lastRing + i;
            indices[t++] = lastRing + n;
        }

        return new ElementMesh(positions, indices);
    }

    private static void Write(float[] positions, int vertex, Vector3 q)
    {
        positions[vertex * 3] = q.X;
        positions[vertex * 3 + 1] = q.Y;
        positions[vertex * 3 + 2] = q.Z;
    }
}
