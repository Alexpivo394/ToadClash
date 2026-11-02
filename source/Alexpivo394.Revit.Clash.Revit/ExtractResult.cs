using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>Результат выгрузки модели.</summary>
/// <param name="Geometry">Геометрия видимых элементов и опись модели.</param>
/// <param name="Parameters">Параметры выгруженных элементов.</param>
/// <param name="Skipped">Элементы, которые не удалось выгрузить, с причиной.</param>
/// <param name="FarElements">
///     Число элементов дальше 1 км от начала выгрузки по любой оси: их координаты в слепке грубее 0,06 мм. Обычно это
///     «улетевшие» элементы модели.
/// </param>
public sealed record ExtractResult(ModelGeometry Geometry, ModelParameters Parameters, IReadOnlyList<SkippedElement> Skipped, int FarElements);

/// <summary>Элемент, который не попал в выгрузку из-за ошибки Revit.</summary>
/// <param name="UniqueId">UniqueId элемента.</param>
/// <param name="ElementId">Id элемента.</param>
/// <param name="Stage">Этап, на котором случилась ошибка: «геометрия» или «параметры».</param>
/// <param name="Message">Сообщение об ошибке.</param>
public sealed record SkippedElement(string UniqueId, long ElementId, string Stage, string Message);
