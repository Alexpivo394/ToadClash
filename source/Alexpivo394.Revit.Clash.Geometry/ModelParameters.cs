namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Параметры элементов модели, сгруппированные по UniqueId.</summary>
public sealed class ModelParameters(IReadOnlyDictionary<string, IReadOnlyList<ParameterValue>> byUniqueId)
{
    private static readonly IReadOnlyList<ParameterValue> NoValues = [];

    /// <summary>Параметры по UniqueId элемента.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ParameterValue>> ByUniqueId { get; } =
        byUniqueId ?? throw new ArgumentNullException(nameof(byUniqueId));

    /// <summary>Параметры элемента; пустой список, если элемент неизвестен.</summary>
    public IReadOnlyList<ParameterValue> For(string uniqueId) =>
        ByUniqueId.TryGetValue(uniqueId, out var values) ? values : NoValues;
}
