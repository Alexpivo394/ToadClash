namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Описание элемента модели.</summary>
/// <param name="RootComponentUniqueId">
///     UniqueId экземпляра семейства верхнего уровня, если элемент - вложенный общий подкомпонент (на любой глубине);
///     иначе null. Элементы с общим корнем - одно изделие и друг с другом не проверяются.
/// </param>
public sealed record ElementInfo(
    string UniqueId,
    long ElementId,
    string Category,
    string? Family,
    string? Type,
    string? Level,
    string? Workset,
    IReadOnlyList<string> ConnectedUniqueIds,
    string? RootComponentUniqueId = null);
