using System.Numerics;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>
///     Выбор ракурса: 8 азимутов через 45° × углы над горизонтом 35° и 65°, первым — юго-западный изометрический (225°, 35°).
///     Каждый кандидат рисуется в 160 × 100 только из A и B; побеждает наибольший min(видимых A, видимых B), при равенстве — первый.
/// </summary>
internal static class ViewChooser
{
    private const int Width = 160;
    private const int Height = 100;

    /// <summary>Направления на камеру в порядке перебора.</summary>
    public static IReadOnlyList<Vector3> Candidates { get; } = BuildCandidates();

    private static Vector3[] BuildCandidates()
    {
        var result = new List<Vector3>();
        foreach (var elevation in new[] { 35.0, 65.0 })
        for (var k = 0; k < 8; k++)
            result.Add(Direction(225 + 45 * k, elevation));
        return result.ToArray();
    }

    /// <summary>Направление на камеру: азимут от оси X против часовой стрелки, угол над горизонтом — в градусах.</summary>
    public static Vector3 Direction(double azimuthDegrees, double elevationDegrees)
    {
        var azimuth = azimuthDegrees * Math.PI / 180;
        var elevation = elevationDegrees * Math.PI / 180;
        return new Vector3(
            (float)(Math.Cos(elevation) * Math.Cos(azimuth)),
            (float)(Math.Cos(elevation) * Math.Sin(azimuth)),
            (float)Math.Sin(elevation));
    }

    public static Vector3 Choose(IReadOnlyList<RenderMesh> meshes, float halfSize)
    {
        var best = Candidates[0];
        var bestScore = -1;
        foreach (var candidate in Candidates)
        {
            var rasterizer = new Rasterizer(new Camera(candidate, halfSize, Width, Height), halfSize, Width, Height, withColor: false);
            foreach (var mesh in meshes)
            {
                if (mesh.Role == RenderRole.A) rasterizer.DrawOpaque(mesh, 1, Palette.A);
                else if (mesh.Role == RenderRole.B) rasterizer.DrawOpaque(mesh, 2, Palette.B);
            }

            var score = Math.Min(rasterizer.Count(1), rasterizer.Count(2));
            if (score <= bestScore) continue;
            bestScore = score;
            best = candidate;
        }

        return best;
    }
}
