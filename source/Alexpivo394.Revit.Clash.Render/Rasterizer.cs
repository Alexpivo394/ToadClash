using System.Numerics;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>Цвет RGB 8 бит.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>Цвета картинки — как в 3D.</summary>
internal static class Palette
{
    public static Rgb Background { get; } = new(0xf8, 0xf9, 0xfa);
    public static Rgb A { get; } = new(0xe0, 0x31, 0x31);
    public static Rgb B { get; } = new(0x19, 0x71, 0xc2);
    public static Rgb Context { get; } = new(0xad, 0xb5, 0xbd);
    public static Rgb Outline { get; } = new(0x21, 0x25, 0x29);
    public static Rgb Ring { get; } = new(0xe8, 0x59, 0x0c);
}

/// <summary>
///     Ортографическая камера: смотрит на начало координат вдоль −<see cref="Back"/>, вверх — ось Z.
///     Масштаб такой, что шар радиуса h вокруг точки коллизии вписан в кадр.
/// </summary>
internal readonly struct Camera
{
    public Camera(Vector3 viewDirection, float halfSize, int width, int height)
    {
        Back = Vector3.Normalize(viewDirection);
        Right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, Back));
        Up = Vector3.Cross(Back, Right);
        // Свет от камеры, чуть сверху-слева: при изометрии грани коробки иначе освещены одинаково
        Light = Vector3.Normalize(Back - 0.35f * Right + 0.5f * Up);
        Scale = Math.Min(width, height) / (2f * halfSize);
        CenterX = width / 2f;
        CenterY = height / 2f;
    }

    public Vector3 Back { get; }
    public Vector3 Right { get; }
    public Vector3 Up { get; }
    public Vector3 Light { get; }
    public float Scale { get; }
    public float CenterX { get; }
    public float CenterY { get; }

    /// <summary>Экранные x, y в пикселях и глубина: больше — ближе к камере.</summary>
    public Vector3 Project(Vector3 p) =>
        new(CenterX + Vector3.Dot(p, Right) * Scale, CenterY - Vector3.Dot(p, Up) * Scale, Vector3.Dot(p, Back));
}

/// <summary>Программная растеризация в буферы цвета, глубины и номера объекта; обрезка секущим кубом.</summary>
internal sealed class Rasterizer
{
    private const float Ambient = 0.35f;
    private const int MaxPolygon = 12;

    private readonly Camera _camera;
    private readonly float _half;
    private readonly float[] _depth;
    private readonly byte[] _ids;
    private readonly byte[]? _rgb;

    public Rasterizer(Camera camera, float halfSize, int width, int height, bool withColor)
    {
        _camera = camera;
        _half = halfSize;
        Width = width;
        Height = height;
        _depth = new float[width * height];
        Array.Fill(_depth, float.NegativeInfinity);
        _ids = new byte[width * height];
        if (!withColor) return;

        _rgb = new byte[width * height * 3];
        var background = Palette.Background;
        for (var i = 0; i < _rgb.Length; i += 3)
        {
            _rgb[i] = background.R;
            _rgb[i + 1] = background.G;
            _rgb[i + 2] = background.B;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels => _rgb ?? throw new InvalidOperationException("Растеризатор без цвета.");

    /// <summary>Число пикселей объекта с номером <paramref name="id"/>.</summary>
    public int Count(byte id)
    {
        var count = 0;
        foreach (var value in _ids)
            if (value == id) count++;
        return count;
    }

    /// <summary>Окружение: смешивание с непрозрачностью 0.25 без проверки и записи глубины.</summary>
    public void DrawContext(RenderMesh mesh) => Draw(mesh, 0, Palette.Context, opaque: false);

    /// <summary>Непрозрачный объект с буфером глубины.</summary>
    public void DrawOpaque(RenderMesh mesh, byte id, Rgb color) => Draw(mesh, id, color, opaque: true);

    private void Draw(RenderMesh mesh, byte id, Rgb color, bool opaque)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        Span<Vector3> polygon = stackalloc Vector3[MaxPolygon];
        Span<Vector3> scratch = stackalloc Vector3[MaxPolygon];
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = Vertex(positions, indices[t]);
            var b = Vertex(positions, indices[t + 1]);
            var c = Vertex(positions, indices[t + 2]);
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length();
            if (!(length > 0)) continue;

            var count = CubeClip.Clip(a, b, c, _half, polygon, scratch);
            if (count < 3) continue;

            var light = Ambient + (1 - Ambient) * MathF.Abs(Vector3.Dot(normal / length, _camera.Light));
            var shade = new Vector3(color.R, color.G, color.B) * light;
            var p0 = _camera.Project(polygon[0]);
            var previous = _camera.Project(polygon[1]);
            for (var k = 2; k < count; k++)
            {
                var current = _camera.Project(polygon[k]);
                Fill(p0, previous, current, id, shade, opaque);
                previous = current;
            }
        }
    }

    private static Vector3 Vertex(float[] positions, int index) =>
        new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);

    private void Fill(Vector3 a, Vector3 b, Vector3 c, byte id, Vector3 shade, bool opaque)
    {
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f) return;
        if (area < 0)
        {
            (b, c) = (c, b);
            area = -area;
        }

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(Width - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(Height - 1, (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY) return;

        // Функции рёбер в центре первого пикселя и их приращения по x и y
        var px = minX + 0.5f;
        var py = minY + 0.5f;
        var edges = new Edges(
            Edge(b, c, px, py), Edge(c, a, px, py), Edge(a, b, px, py),
            -(c.Y - b.Y), -(a.Y - c.Y), -(b.Y - a.Y),
            c.X - b.X, a.X - c.X, b.X - a.X);

        if (opaque) FillOpaque(edges, minX, maxX, minY, maxY, a.Z / area, b.Z / area, c.Z / area, id, shade);
        else if (_rgb is not null) FillContext(edges, minX, maxX, minY, maxY, shade);
    }

    private readonly record struct Edges(float W0, float W1, float W2, float W0Dx, float W1Dx, float W2Dx, float W0Dy, float W1Dy, float W2Dy);

    private void FillOpaque(Edges e, int minX, int maxX, int minY, int maxY, float za, float zb, float zc, byte id, Vector3 shade)
    {
        var rgb = _rgb;
        var r = (byte)shade.X;
        var g = (byte)shade.Y;
        var b = (byte)shade.Z;
        float w0Row = e.W0, w1Row = e.W1, w2Row = e.W2;
        for (var y = minY; y <= maxY; y++, w0Row += e.W0Dy, w1Row += e.W1Dy, w2Row += e.W2Dy)
        {
            float w0 = w0Row, w1 = w1Row, w2 = w2Row;
            var row = y * Width;
            var entered = false;
            for (var x = minX; x <= maxX; x++, w0 += e.W0Dx, w1 += e.W1Dx, w2 += e.W2Dx)
            {
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    if (entered) break;
                    continue;
                }

                entered = true;
                var pixel = row + x;
                var depth = w0 * za + w1 * zb + w2 * zc;
                if (depth <= _depth[pixel]) continue;
                _depth[pixel] = depth;
                _ids[pixel] = id;
                if (rgb is null) continue;
                var i = pixel * 3;
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
        }
    }

    /// <summary>Смешивание с непрозрачностью 1/4: (3·фон + цвет) / 4 в целых.</summary>
    private void FillContext(Edges e, int minX, int maxX, int minY, int maxY, Vector3 shade)
    {
        var rgb = _rgb!;
        var r = (int)shade.X;
        var g = (int)shade.Y;
        var b = (int)shade.Z;
        float w0Row = e.W0, w1Row = e.W1, w2Row = e.W2;
        for (var y = minY; y <= maxY; y++, w0Row += e.W0Dy, w1Row += e.W1Dy, w2Row += e.W2Dy)
        {
            float w0 = w0Row, w1 = w1Row, w2 = w2Row;
            var row = y * Width;
            var entered = false;
            for (var x = minX; x <= maxX; x++, w0 += e.W0Dx, w1 += e.W1Dx, w2 += e.W2Dx)
            {
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    if (entered) break;
                    continue;
                }

                entered = true;
                var i = (row + x) * 3;
                rgb[i] = (byte)((rgb[i] * 3 + r) >> 2);
                rgb[i + 1] = (byte)((rgb[i + 1] * 3 + g) >> 2);
                rgb[i + 2] = (byte)((rgb[i + 2] * 3 + b) >> 2);
            }
        }
    }
    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    /// <summary>Обводка: пиксель A или B, у соседа которого (4-связность) другой номер объекта.</summary>
    public void Outline()
    {
        var rgb = Pixels;
        var outline = Palette.Outline;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var pixel = y * Width + x;
            var id = _ids[pixel];
            if (id == 0) continue;
            var edge = (x > 0 && _ids[pixel - 1] != id) || (x < Width - 1 && _ids[pixel + 1] != id) ||
                       (y > 0 && _ids[pixel - Width] != id) || (y < Height - 1 && _ids[pixel + Width] != id);
            if (!edge) continue;
            rgb[pixel * 3] = outline.R;
            rgb[pixel * 3 + 1] = outline.G;
            rgb[pixel * 3 + 2] = outline.B;
        }
    }

    /// <summary>Кольцо радиусом 8 пикселей в центре кадра — точка коллизии.</summary>
    public void Ring(float radius = 8f, float thickness = 2f)
    {
        var rgb = Pixels;
        var ring = Palette.Ring;
        var inner = (radius - thickness / 2) * (radius - thickness / 2);
        var outer = (radius + thickness / 2) * (radius + thickness / 2);
        var cx = _camera.CenterX;
        var cy = _camera.CenterY;
        var reach = (int)MathF.Ceiling(radius + thickness);
        for (var y = Math.Max(0, (int)cy - reach); y <= Math.Min(Height - 1, (int)cy + reach); y++)
        for (var x = Math.Max(0, (int)cx - reach); x <= Math.Min(Width - 1, (int)cx + reach); x++)
        {
            var dx = x + 0.5f - cx;
            var dy = y + 0.5f - cy;
            var d = dx * dx + dy * dy;
            if (d < inner || d >= outer) continue;
            var i = (y * Width + x) * 3;
            rgb[i] = ring.R;
            rgb[i + 1] = ring.G;
            rgb[i + 2] = ring.B;
        }
    }
}

/// <summary>Обрезка треугольника кубом [−h, h]³ (Сазерленд–Ходжман по шести плоскостям).</summary>
internal static class CubeClip
{
    /// <summary>Записывает многоугольник в <paramref name="output"/> и возвращает число вершин (0 — треугольник вне куба).</summary>
    public static int Clip(Vector3 a, Vector3 b, Vector3 c, float h, Span<Vector3> output, Span<Vector3> scratch)
    {
        if (Inside(a, h) && Inside(b, h) && Inside(c, h))
        {
            output[0] = a;
            output[1] = b;
            output[2] = c;
            return 3;
        }

        for (var axis = 0; axis < 3; axis++)
        {
            if (a[axis] > h && b[axis] > h && c[axis] > h) return 0;
            if (a[axis] < -h && b[axis] < -h && c[axis] < -h) return 0;
        }

        output[0] = a;
        output[1] = b;
        output[2] = c;
        var count = 3;
        var source = output;
        var target = scratch;
        for (var plane = 0; plane < 6 && count >= 3; plane++)
        {
            var axis = plane >> 1;
            var sign = (plane & 1) == 0 ? 1f : -1f;
            var written = 0;
            for (var i = 0; i < count; i++)
            {
                var current = source[i];
                var next = source[(i + 1) % count];
                var dc = h - sign * current[axis];
                var dn = h - sign * next[axis];
                if (dc >= 0) target[written++] = current;
                if ((dc >= 0) != (dn >= 0)) target[written++] = current + (next - current) * (dc / (dc - dn));
            }

            count = written;
            var swap = source;
            source = target;
            target = swap;
        }

        if (count < 3) return 0;
        if (source != output) source[..count].CopyTo(output);
        return count;
    }

    private static bool Inside(Vector3 p, float h) =>
        p.X >= -h && p.X <= h && p.Y >= -h && p.Y <= h && p.Z >= -h && p.Z <= h;
}
