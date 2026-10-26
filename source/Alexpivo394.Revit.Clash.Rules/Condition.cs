using System.Globalization;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Оператор сравнения в условии.</summary>
public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    Greater,
    Less,
    IsEmpty,
    IsNotEmpty
}

/// <summary>
///     Условие на свойство элемента. Строки сравниваются без учёта регистра; числа разбираются с точкой или запятой.
///     Если свойства нет, <see cref="ConditionOperator.IsEmpty"/>, <see cref="ConditionOperator.NotEquals"/> и
///     <see cref="ConditionOperator.NotContains"/> дают true, остальные операторы - false.
/// </summary>
public sealed record Condition(PropertyRef Property, ConditionOperator Operator, string? Value)
{
    /// <summary>Проверяет, удовлетворяет ли элемент условию.</summary>
    /// <param name="info">Описание элемента.</param>
    /// <param name="parameters">Параметры элемента.</param>
    public bool Matches(ElementInfo info, IReadOnlyList<ParameterValue> parameters)
    {
        if (info is null) throw new ArgumentNullException(nameof(info));
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));

        var (text, number) = Resolve(info, parameters);
        var isEmpty = string.IsNullOrWhiteSpace(text) && number is null;
        var expected = Value ?? "";

        return Operator switch
        {
            ConditionOperator.IsEmpty => isEmpty,
            ConditionOperator.IsNotEmpty => !isEmpty,
            ConditionOperator.Equals => text is not null && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => text is null || !string.Equals(text, expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => text is not null && text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0,
            ConditionOperator.NotContains => text is null || text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0,
            ConditionOperator.Greater => Compare(text, number) is { } greater && greater > 0,
            ConditionOperator.Less => Compare(text, number) is { } less && less < 0,
            _ => false
        };
    }

    private (string? Text, double? Number) Resolve(ElementInfo info, IReadOnlyList<ParameterValue> parameters)
    {
        switch (Property.Kind)
        {
            case PropertyKind.Category: return (info.Category, null);
            case PropertyKind.Family: return (info.Family, null);
            case PropertyKind.Type: return (info.Type, null);
            case PropertyKind.Level: return (info.Level, null);
            case PropertyKind.Workset: return (info.Workset, null);
        }

        var parameter = FindParameter(parameters);
        if (parameter is null) return (null, null);

        var text = parameter.Text ?? parameter.Number?.ToString("R", CultureInfo.InvariantCulture);
        return (text, parameter.Number);
    }

    private ParameterValue? FindParameter(IReadOnlyList<ParameterValue> parameters)
    {
        if (Property.SharedGuid is { } guid)
        {
            foreach (var parameter in parameters)
                if (parameter.SharedGuid == guid) return parameter;
        }

        if (string.IsNullOrEmpty(Property.ParameterName)) return null;
        foreach (var parameter in parameters)
            if (string.Equals(parameter.Name, Property.ParameterName, StringComparison.OrdinalIgnoreCase)) return parameter;

        return null;
    }

    private int? Compare(string? text, double? number)
    {
        if (!TryParse(Value, out var expected)) return null;
        if (number is { } actual) return actual.CompareTo(expected);
        return TryParse(text, out actual) ? actual.CompareTo(expected) : null;
    }

    private static bool TryParse(string? s, out double value)
    {
        value = 0;
        return s is not null && double.TryParse(s.Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
