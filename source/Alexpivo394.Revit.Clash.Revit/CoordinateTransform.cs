namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>Переводит точки Revit (футы, внутренняя система проекта) в мм общей системы координат документа.</summary>
internal sealed class CoordinateTransform
{
    private const double FeetToMillimeters = 304.8;
    private const double OriginStepMm = 1000;

    private readonly Transform _toShared;

    private CoordinateTransform(Transform toShared) => _toShared = toShared;

    /// <summary>Общие координаты активной площадки документа.</summary>
    public static CoordinateTransform FromDocument(Document document) =>
        new(document.ActiveProjectLocation.GetTotalTransform().Inverse);

    /// <summary>Точка в мм общей системы, округлённая до метра: начало локальных координат выгрузки.</summary>
    public Vector3d RoundedOrigin(XYZ point)
    {
        var shared = _toShared.OfPoint(point);
        return new Vector3d(Round(shared.X), Round(shared.Y), Round(shared.Z));

        static double Round(double feet) => Math.Round(feet * FeetToMillimeters / OriginStepMm) * OriginStepMm;
    }

    /// <summary>
    ///     Вершины, заданные в системе <paramref name="local"/> (накопленная трансформация вложенных экземпляров),
    ///     в абсолютных мм общей системы. Трансформация применяется вручную, без вызова Revit на каждую точку.
    /// </summary>
    public Vector3d[] ToSharedMillimeters(IList<XYZ> points, Transform local)
    {
        var total = _toShared.Multiply(local);
        var origin = total.Origin;
        var basisX = total.BasisX;
        var basisY = total.BasisY;
        var basisZ = total.BasisZ;
        double ox = origin.X, oy = origin.Y, oz = origin.Z;
        double xx = basisX.X, xy = basisX.Y, xz = basisX.Z;
        double yx = basisY.X, yy = basisY.Y, yz = basisY.Z;
        double zx = basisZ.X, zy = basisZ.Y, zz = basisZ.Z;

        var result = new Vector3d[points.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var point = points[i];
            double px = point.X, py = point.Y, pz = point.Z;
            result[i] = new Vector3d(
                (ox + px * xx + py * yx + pz * zx) * FeetToMillimeters,
                (oy + px * xy + py * yy + pz * zy) * FeetToMillimeters,
                (oz + px * xz + py * yz + pz * zz) * FeetToMillimeters);
        }

        return result;
    }
}
