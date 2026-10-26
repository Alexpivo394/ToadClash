namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Выгруженная геометрия одной модели.</summary>
public sealed class ModelGeometry(ModelHeader header, IReadOnlyList<ElementGeometry> elements, IReadOnlyList<string> inventory)
{
    /// <summary>Заголовок выгрузки.</summary>
    public ModelHeader Header { get; } = header;

    /// <summary>Элементы с геометрией.</summary>
    public IReadOnlyList<ElementGeometry> Elements { get; } = elements;

    /// <summary>Перечень UniqueId всех элементов модели, подлежавших выгрузке.</summary>
    public IReadOnlyList<string> Inventory { get; } = inventory;
}
