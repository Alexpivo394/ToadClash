using System.Diagnostics;
using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Render;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Render;

public sealed class CollisionRendererTests
{
    private static RenderMesh Mesh(RenderRole role, ElementMesh mesh) => new(role, mesh.Positions, mesh.Indices);

    private static RenderMesh Box(RenderRole role, Vector3 min, Vector3 max) => Mesh(role, TestScenes.Box(min, max));

    private static RenderMesh Pipe(RenderRole role, Vector3 a, Vector3 b, float radius) => Mesh(role, TestScenes.Cylinder(a, b, radius, 24));

    private static Aabb Bounds(RenderMesh mesh) => Aabb.FromPositions(mesh.Positions);

    private sealed class Picture(byte[] png)
    {
        private readonly (int Width, int Height, byte[] Rgb) _image = PngTestReader.Decode(png);

        public int Width => _image.Width;
        public int Height => _image.Height;

        public (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (y * Width + x) * 3;
            return (_image.Rgb[i], _image.Rgb[i + 1], _image.Rgb[i + 2]);
        }

        public IEnumerable<(int X, int Y, (byte R, byte G, byte B) C)> Pixels()
        {
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                yield return (x, y, At(x, y));
        }

        public int Count(Func<(byte R, byte G, byte B), bool> match) => Pixels().Count(p => match(p.C));

        public int CountNearCenter(int radius, Func<(byte R, byte G, byte B), bool> match) =>
            Pixels().Count(p => (p.X - Width / 2) * (p.X - Width / 2) + (p.Y - Height / 2) * (p.Y - Height / 2) <= radius * radius && match(p.C));
    }

    private static bool IsRed((byte R, byte G, byte B) c) => c.R > c.G + 30 && Math.Abs(c.G - c.B) <= 12;

    private static bool IsBlue((byte R, byte G, byte B) c) => c.B > c.R + 30 && c.B > c.G + 20;

    private static bool IsRing((byte R, byte G, byte B) c) => c == (0xe8, 0x59, 0x0c);

    private static bool IsBackground((byte R, byte G, byte B) c) => c == (0xf8, 0xf9, 0xfa);

    private static (RenderedImage Image, Picture Picture) Render(params RenderMesh[] meshes)
    {
        var a = meshes.Where(m => m.Role == RenderRole.A).Select(Bounds).DefaultIfEmpty(Aabb.Empty).Aggregate((x, y) => x.Union(y));
        var b = meshes.Where(m => m.Role == RenderRole.B).Select(Bounds).DefaultIfEmpty(Aabb.Empty).Aggregate((x, y) => x.Union(y));
        var image = CollisionRenderer.Render(meshes, CollisionRenderer.HalfSize(a, b));
        return (image, new Picture(image.Png));
    }

    [Test]
    public void Render_OverlappingBoxes_CenterShowsPair()
    {
        var (image, picture) = Render(
            Box(RenderRole.A, new Vector3(-150), new Vector3(50)),
            Box(RenderRole.B, new Vector3(-50), new Vector3(150)));

        image.Width.ShouldBe(640);
        image.Height.ShouldBe(400);
        picture.Width.ShouldBe(640);
        picture.CountNearCenter(20, IsRed).ShouldBeGreaterThan(0);
        picture.CountNearCenter(20, IsBlue).ShouldBeGreaterThan(0);
        picture.Count(IsRing).ShouldBe(0); // без кольца в точке коллизии: по заказчику оно лишнее
    }

    [Test]
    public void Render_ContextInFront_PairStillVisible()
    {
        // Полая коробка окружения вокруг пары: с любого ракурса она между камерой и парой
        var (_, picture) = Render(
            Box(RenderRole.A, new Vector3(-150), new Vector3(50)),
            Box(RenderRole.B, new Vector3(-50), new Vector3(150)),
            Box(RenderRole.Context, new Vector3(-500), new Vector3(500)));

        var center = picture.At(picture.Width / 2 + 3, picture.Height / 2 + 3);
        (IsRed(center) || IsBlue(center)).ShouldBeTrue($"центр {center}");
        picture.CountNearCenter(20, IsRed).ShouldBeGreaterThan(0);
        picture.CountNearCenter(20, IsBlue).ShouldBeGreaterThan(0);

        // Окружение видно, но только осветляет фон: пиксели вне пары светлые и не равны фону
        var contextPixels = picture.Pixels().Where(p => !IsBackground(p.C) && !IsRed(p.C) && !IsBlue(p.C) && !IsRing(p.C)
                                                        && Math.Abs(p.X - picture.Width / 2) > 120).ToList();
        contextPixels.Count.ShouldBeGreaterThan(1000);
        contextPixels.ShouldAllBe(p => p.C.R > 150 && p.C.G > 150 && p.C.B > 150);
    }

    [Test]
    public void Render_LongPipe_ClashFillsFrame()
    {
        var pipe = Pipe(RenderRole.B, new Vector3(-25_000, 0, 0), new Vector3(25_000, 0, 0), 50);
        var fitting = Box(RenderRole.A, new Vector3(-100), new Vector3(100));

        CollisionRenderer.HalfSize(Bounds(fitting), Bounds(pipe)).ShouldBeLessThanOrEqualTo(600);
        var (_, picture) = Render(fitting, pipe);

        picture.Count(IsRed).ShouldBeGreaterThan(640 * 400 / 50);
    }

    [Test]
    public void Render_PlateHidesPipe_PicksViewShowingBoth()
    {
        // Стена 3 × 3 м толщиной 200 мм развёрнута лицом на юго-запад, труба выходит из её середины на северо-восток.
        // С юго-запада сверху (первый кандидат) труба закрыта стеной.
        var normal = Vector3.Normalize(new Vector3(1, 1, 0));
        var along = Vector3.Normalize(new Vector3(1, -1, 0));
        var corners = new List<Vector3>();
        foreach (var t in new[] { -100f, 100f })
        foreach (var s in new[] { -1500f, 1500f })
        foreach (var z in new[] { -1500f, 1500f })
            corners.Add(normal * t + along * s + new Vector3(0, 0, z));
        var wall = WallMesh(corners);
        var pipe = Pipe(RenderRole.B, Vector3.Zero, normal * 1500, 50);

        var image = CollisionRenderer.Render([wall, pipe], 600);
        var picture = new Picture(image.Png);

        picture.Count(IsRed).ShouldBeGreaterThan(200);
        picture.Count(IsBlue).ShouldBeGreaterThan(200);
        Vector3.Dot(image.ViewDirection, normal).ShouldBeGreaterThan(0f, "камера должна смотреть с северо-восточной стороны стены");
    }

    [Test]
    public void Render_PipeInsideClosedBox_HiddenSideHatched()
    {
        // Труба целиком внутри короба: ни с одного ракурса её не видно — скрытая часть показывается штриховкой своего цвета.
        var box = Box(RenderRole.B, new Vector3(-500, -200, -200), new Vector3(500, 200, 200));
        var pipe = Pipe(RenderRole.A, new Vector3(-400, 0, 0), new Vector3(400, 0, 0), 50);

        var picture = new Picture(CollisionRenderer.Render([pipe, box], 600).Png);

        picture.Count(IsRed).ShouldBeGreaterThan(200);
        picture.Count(IsBlue).ShouldBeGreaterThan(200);
    }

    private static RenderMesh WallMesh(List<Vector3> c)
    {
        // Порядок углов: t (2) × s (2) × z (2) — индекс = t*4 + s*2 + z
        var positions = c.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
        int[] indices =
        [
            0, 1, 3, 0, 3, 2, // t = -100
            4, 7, 5, 4, 6, 7, // t = +100
            0, 4, 5, 0, 5, 1, // s = -1500
            2, 3, 7, 2, 7, 6, // s = +1500
            0, 2, 6, 0, 6, 4, // z = -1500
            1, 5, 7, 1, 7, 3 // z = +1500
        ];
        return new RenderMesh(RenderRole.A, positions, indices);
    }

    [Test]
    public void Render_SameInput_SameBytes()
    {
        RenderMesh[] scene =
        [
            Box(RenderRole.A, new Vector3(-150), new Vector3(50)),
            Pipe(RenderRole.B, new Vector3(-800, 0, 0), new Vector3(800, 0, 0), 60),
            Box(RenderRole.Context, new Vector3(-700, -700, -300), new Vector3(700, 700, -250))
        ];

        CollisionRenderer.Render(scene, 600).Png.ShouldBe(CollisionRenderer.Render(scene, 600).Png);
    }

    [Test]
    public void Render_NoTrianglesForB_StillRenders()
    {
        var image = CollisionRenderer.Render([Box(RenderRole.A, new Vector3(-150), new Vector3(50)), new RenderMesh(RenderRole.B, [], [])], 600);

        new Picture(image.Png).Count(IsRed).ShouldBeGreaterThan(0);
    }

    [Test]
    public void Render_TrianglesCrossingBox_Clipped()
    {
        // Горизонтальный треугольник 100 × 100 м через центр: после обрезки — квадрат 1200 × 1200 мм
        var huge = new RenderMesh(RenderRole.A, [-50_000, -50_000, 0, 50_000, -50_000, 0, 0, 50_000, 0], [0, 1, 2]);

        var image = CollisionRenderer.Render([huge, new RenderMesh(RenderRole.B, [], [])], 600);
        var picture = new Picture(image.Png);

        picture.CountNearCenter(20, IsRed).ShouldBeGreaterThan(0);
        picture.Pixels().Where(p => p.Y < 30 || p.Y >= picture.Height - 30).ShouldAllBe(p => IsBackground(p.C));
    }

    [Test]
    public void HalfSize_ClampsAndAddsMargin()
    {
        static Aabb Cube(float half) => new(new Vector3(-half), new Vector3(half));

        CollisionRenderer.HalfSize(Cube(50), Cube(5000)).ShouldBe(600);
        CollisionRenderer.HalfSize(Cube(1000), Cube(5000)).ShouldBe(1400);
        CollisionRenderer.HalfSize(Cube(5000), Cube(5000)).ShouldBe(3000);
        CollisionRenderer.HalfSize(Cube(10), new Aabb(new Vector3(100), new Vector3(200))).ShouldBe(600);
    }

    [Test]
    [Explicit]
    public void Render_300kContextTriangles_Under80ms()
    {
        var random = new Random(11);
        var context = new List<float>();
        var indices = new List<int>();
        for (var i = 0; i < 300_000; i++)
        {
            var center = new Vector3(random.NextSingle() * 2400 - 1200, random.NextSingle() * 2400 - 1200, random.NextSingle() * 2400 - 1200);
            for (var k = 0; k < 3; k++)
            {
                var v = center + new Vector3(random.NextSingle() * 80 - 40, random.NextSingle() * 80 - 40, random.NextSingle() * 80 - 40);
                context.AddRange([v.X, v.Y, v.Z]);
                indices.Add(indices.Count);
            }
        }

        RenderMesh[] scene =
        [
            Box(RenderRole.A, new Vector3(-150), new Vector3(50)),
            Pipe(RenderRole.B, new Vector3(-800, 0, 0), new Vector3(800, 0, 0), 60),
            new(RenderRole.Context, context.ToArray(), indices.ToArray())
        ];

        CollisionRenderer.Render(scene, 1000);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) CollisionRenderer.Render(scene, 1000);
        var average = watch.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"Среднее на картинку: {average:F1} мс");
        average.ShouldBeLessThan(80);
    }
}
