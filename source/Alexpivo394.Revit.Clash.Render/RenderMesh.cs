using System.Numerics;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>Роль сетки на картинке: элемент A, элемент B или окружение.</summary>
public enum RenderRole
{
    /// <summary>Элемент A коллизии.</summary>
    A,

    /// <summary>Элемент B коллизии.</summary>
    B,

    /// <summary>Окружение, рисуется полупрозрачно.</summary>
    Context
}

/// <summary>Сетка для картинки. Позиции — тройки xyz в мм относительно точки коллизии, индексы — тройки треугольников.</summary>
public sealed record RenderMesh(RenderRole Role, float[] Positions, int[] Indices);

/// <summary>Размер картинки в пикселях.</summary>
public sealed record RenderOptions(int Width = 640, int Height = 400);

/// <summary>Готовая картинка: PNG, размер, половина стороны секущего куба и направление на камеру.</summary>
public sealed record RenderedImage(byte[] Png, int Width, int Height, double HalfSizeMm, Vector3 ViewDirection);
