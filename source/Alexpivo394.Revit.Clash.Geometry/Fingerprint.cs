using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Отпечаток геометрии для обнаружения изменений элемента.</summary>
public static class Fingerprint
{
    private const double QuantumMm = 0.1;

    /// <summary>
    ///     XxHash64 по абсолютным координатам вершин (Origin + позиция), квантованным с шагом 0.1 мм,
    ///     затем по индексам.
    /// </summary>
    public static ulong Compute(ElementMesh mesh, ModelHeader header)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        if (header is null) throw new ArgumentNullException(nameof(header));

        var positions = mesh.Positions;
        var quantised = new long[positions.Length];
        for (var i = 0; i < positions.Length; i += 3)
        {
            quantised[i] = Quantise(header.OriginX, positions[i]);
            quantised[i + 1] = Quantise(header.OriginY, positions[i + 1]);
            quantised[i + 2] = Quantise(header.OriginZ, positions[i + 2]);
        }

        var hash = new XxHash64();
        hash.Append(MemoryMarshal.AsBytes(quantised.AsSpan()));
        hash.Append(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
        return hash.GetCurrentHashAsUInt64();
    }

    private static long Quantise(double origin, float position) =>
        (long)Math.Round((origin + position) / QuantumMm);
}
