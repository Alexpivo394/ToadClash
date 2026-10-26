namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Элемент проверки: модель и UniqueId. Порядок: сначала ModelKey, затем UniqueId, ordinal.</summary>
public readonly record struct ElementRef(string ModelKey, string UniqueId) : IComparable<ElementRef>
{
    /// <summary>Каноническое сравнение: ordinal по ModelKey, затем по UniqueId.</summary>
    public int CompareTo(ElementRef other)
    {
        var byModel = string.CompareOrdinal(ModelKey, other.ModelKey);
        return byModel != 0 ? byModel : string.CompareOrdinal(UniqueId, other.UniqueId);
    }
}
