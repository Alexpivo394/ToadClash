namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Значение свойства и у скольких элементов оно встретилось.</summary>
public sealed record ValueCount(string Value, int Count);

/// <summary>Подсказки значений условия: что реально встречается у элементов источника группы.</summary>
public static class PropertyValues
{
    /// <returns>Значения по убыванию числа элементов, при равенстве — по алфавиту; пустые не учитываются.</returns>
    public static IReadOnlyList<ValueCount> Count(ClashConfiguration config, Guid groupId, PropertyRef property, IReadOnlyList<ElementsInput> models,
        int limit = 200)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (property is null) throw new ArgumentNullException(nameof(property));
        if (models is null) throw new ArgumentNullException(nameof(models));

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, info) in Elements(config, groupId, models))
        {
            var (text, _) = Condition.Resolve(property, info, model.Parameters.For(info.UniqueId));
            if (string.IsNullOrWhiteSpace(text)) continue;
            counts[text!] = counts.TryGetValue(text!, out var n) ? n + 1 : 1;
        }

        return Ordered(counts, limit);
    }

    /// <summary>Элементы источника группы.</summary>
    internal static IEnumerable<(ElementsInput Model, Geometry.ElementInfo Info)> Elements(ClashConfiguration config, Guid groupId,
        IReadOnlyList<ElementsInput> models)
    {
        var group = config.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null) yield break;
        foreach (var model in models)
        {
            if (!group.Source.Includes(model.Discipline, model.ModelKey)) continue;
            foreach (var info in model.Elements) yield return (model, info);
        }
    }

    internal static IReadOnlyList<ValueCount> Ordered(Dictionary<string, int> counts, int limit) =>
        counts.Select(p => new ValueCount(p.Key, p.Value))
            .OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, limit)).ToList();
}
