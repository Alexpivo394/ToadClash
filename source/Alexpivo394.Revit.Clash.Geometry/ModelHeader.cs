namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Заголовок выгрузки модели. Origin - смещение начала локальных координат, мм, в общей системе модели.</summary>
public sealed record ModelHeader(
    string ModelKey,
    string SourcePath,
    int RevitYear,
    DateTimeOffset ExtractedAt,
    bool Complete,
    string ViewName,
    double OriginX,
    double OriginY,
    double OriginZ);
