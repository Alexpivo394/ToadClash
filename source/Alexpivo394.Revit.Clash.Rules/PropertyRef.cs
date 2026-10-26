namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Вид свойства элемента, по которому задаётся условие.</summary>
public enum PropertyKind
{
    /// <summary>Категория (имя BuiltInCategory).</summary>
    Category,

    /// <summary>Семейство.</summary>
    Family,

    /// <summary>Типоразмер.</summary>
    Type,

    /// <summary>Уровень.</summary>
    Level,

    /// <summary>Рабочий набор.</summary>
    Workset,

    /// <summary>Параметр элемента.</summary>
    Parameter
}

/// <summary>
///     Ссылка на свойство элемента. Для <see cref="PropertyKind.Parameter"/> параметр ищется сначала по
///     <paramref name="SharedGuid"/>, затем по <paramref name="ParameterName"/> без учёта регистра.
/// </summary>
public sealed record PropertyRef(PropertyKind Kind, string? ParameterName = null, Guid? SharedGuid = null);
