namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>
///     Параметры выгрузки модели.
/// </summary>
/// <param name="ModelKey">Ключ модели в проверке, попадает в заголовок выгрузки.</param>
/// <param name="ViewName">Имя 3D-вида, видимость на котором определяет состав выгружаемой геометрии.</param>
/// <param name="TriangulationLevelOfDetail">Детализация триангуляции граней, от 0 (грубо) до 1 (точно).</param>
public sealed record ExtractOptions(string ModelKey, string ViewName, double TriangulationLevelOfDetail = 0.5);
