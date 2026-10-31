using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>
///     Картинка коллизии на процессоре. Ортографическая камера смотрит на точку коллизии, геометрия обрезана секущим кубом.
///     Сначала окружение полупрозрачно без глубины, потом A и B непрозрачно со своим буфером глубины и обводка.
/// </summary>
public static class CollisionRenderer
{
    /// <summary>Запас к половине наибольшего размера пересечения габаритов, мм.</summary>
    public const double MarginMm = 400;

    /// <summary>Наименьшая половина стороны секущего куба, мм.</summary>
    public const double MinHalfSizeMm = 600;

    /// <summary>Наибольшая половина стороны секущего куба, мм.</summary>
    public const double MaxHalfSizeMm = 3000;

    /// <summary>Рисует сцену, собранную <see cref="CollisionScenes"/>.</summary>
    public static RenderedImage Render(RenderScene scene, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Render(scene.Meshes, scene.HalfSizeMm, options);
    }

    /// <summary>Рисует коллизию. Позиции сеток — относительно точки коллизии, мм.</summary>
    public static RenderedImage Render(IReadOnlyList<RenderMesh> meshes, double halfSizeMm, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        options ??= new RenderOptions();
        if (options.Width <= 0 || options.Height <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (!(halfSizeMm > 0)) throw new ArgumentOutOfRangeException(nameof(halfSizeMm));

        var half = (float)halfSizeMm;
        var view = ViewChooser.Choose(meshes, half);
        var rasterizer = new Rasterizer(new Camera(view, half, options.Width, options.Height), half, options.Width, options.Height, withColor: true);
        foreach (var mesh in meshes)
            if (mesh.Role == RenderRole.Context) rasterizer.DrawContext(mesh);
        foreach (var mesh in meshes)
        {
            if (mesh.Role == RenderRole.A) rasterizer.DrawOpaque(mesh, 1, Palette.A);
            else if (mesh.Role == RenderRole.B) rasterizer.DrawOpaque(mesh, 2, Palette.B);
        }

        rasterizer.HatchHidden(meshes.Where(m => m.Role == RenderRole.A), 1, Palette.A);
        rasterizer.HatchHidden(meshes.Where(m => m.Role == RenderRole.B), 2, Palette.B);
        rasterizer.Outline();
        return new RenderedImage(Png.Encode(rasterizer.Pixels, options.Width, options.Height), options.Width, options.Height, halfSizeMm, view);
    }

    /// <summary>
    ///     Половина стороны секущего куба: половина наибольшего размера пересечения габаритов A и B плюс <see cref="MarginMm"/>,
    ///     в пределах <see cref="MinHalfSizeMm"/>–<see cref="MaxHalfSizeMm"/>. Габариты — относительно точки коллизии.
    /// </summary>
    public static double HalfSize(Aabb a, Aabb b) => HalfSize(new Aabb(Vector3.Max(a.Min, b.Min), Vector3.Min(a.Max, b.Max)));

    /// <summary>
    ///     Половина стороны секущего куба по зоне пересечения тел (<see cref="Engine.IntersectionZone"/>): половина её
    ///     наибольшего размера плюс <see cref="MarginMm"/>, в пределах <see cref="MinHalfSizeMm"/>–<see cref="MaxHalfSizeMm"/>.
    /// </summary>
    public static double HalfSize(Aabb zone)
    {
        if (!(zone.Min.X <= zone.Max.X && zone.Min.Y <= zone.Max.Y && zone.Min.Z <= zone.Max.Z)) return MinHalfSizeMm;

        var size = zone.Max - zone.Min;
        var largest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        return Math.Clamp(largest / 2.0 + MarginMm, MinHalfSizeMm, MaxHalfSizeMm);
    }
}
