namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Параметры движка.</summary>
/// <param name="MaxDegreeOfParallelism">Число потоков; 0 - все ядра.</param>
public sealed record EngineOptions(int MaxDegreeOfParallelism = 0);

/// <summary>Найденная коллизия: элементы в каноническом порядке (A меньше B), точка в абсолютных мм общей системы, глубина.</summary>
/// <param name="A">Меньший элемент пары.</param>
/// <param name="B">Больший элемент пары.</param>
/// <param name="PointX">Точка коллизии (центр зоны пересечения), мм.</param>
/// <param name="PointY">Точка коллизии (центр зоны пересечения), мм.</param>
/// <param name="PointZ">Точка коллизии (центр зоны пересечения), мм.</param>
/// <param name="DepthMm">Оценка глубины проникновения, мм.</param>
public sealed record ClashHit(ElementRef A, ElementRef B, double PointX, double PointY, double PointZ, float DepthMm);

/// <summary>Статистика прогона.</summary>
/// <param name="Elements">Число элементов сцены.</param>
/// <param name="Triangles">Число треугольников элементов сцены.</param>
/// <param name="CandidatePairs">Число пар после широкой фазы.</param>
/// <param name="BroadPhase">Время широкой фазы.</param>
/// <param name="NarrowPhase">Время точной фазы.</param>
/// <param name="PeakManagedBytes">Наибольший замер управляемой кучи в контрольных точках, байт.</param>
public sealed record ClashRunStats(
    int Elements,
    long Triangles,
    long CandidatePairs,
    TimeSpan BroadPhase,
    TimeSpan NarrowPhase,
    long PeakManagedBytes);

/// <summary>Результат прогона: коллизии, отсортированные по (A, B), и статистика.</summary>
public sealed record ClashRunResult(IReadOnlyList<ClashHit> Hits, ClashRunStats Stats);
