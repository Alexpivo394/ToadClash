namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Описание элемента модели.</summary>
public sealed record ElementInfo(
    string UniqueId,
    long ElementId,
    string Category,
    string? Family,
    string? Type,
    string? Level,
    string? Workset,
    IReadOnlyList<string> ConnectedUniqueIds);
