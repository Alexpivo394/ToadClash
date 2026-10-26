using System.Runtime.CompilerServices;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Вектор в double для точных расчётов точной фазы.</summary>
internal readonly record struct Vec3d(double X, double Y, double Z)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d At(float[] positions, int vertex)
    {
        var i = vertex * 3;
        return new Vec3d(positions[i], positions[i + 1], positions[i + 2]);
    }

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3d operator *(Vec3d a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3d Cross(Vec3d a, Vec3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3d Normalized()
    {
        var length = Math.Sqrt(LengthSquared);
        return new Vec3d(X / length, Y / length, Z / length);
    }
}
