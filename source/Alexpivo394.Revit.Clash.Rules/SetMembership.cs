using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>
///     Для каждого элемента — Id наборов, в которые он попал, и группы этих наборов. Набор смотрит только на элементы
///     источника своей группы. Элемент без наборов в проверке не участвует.
/// </summary>
public sealed class SetMembership
{
    private static readonly IReadOnlyList<Guid> NoSets = [];

    private readonly Dictionary<(string ModelKey, string UniqueId), (IReadOnlyList<Guid> Sets, IReadOnlyList<Guid> Groups)> _sets;

    private SetMembership(Dictionary<(string ModelKey, string UniqueId), (IReadOnlyList<Guid>, IReadOnlyList<Guid>)> sets) => _sets = sets;

    /// <summary>Вычисляет принадлежность элементов наборам.</summary>
    public static SetMembership Evaluate(ClashConfiguration config, IReadOnlyList<ModelInput> models)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        return Evaluate(config, models.Select(m => m.ToElements()).ToArray());
    }

    /// <summary>То же по описаниям элементов без сеток — например, по оглавлению выгрузки.</summary>
    public static SetMembership Evaluate(ClashConfiguration config, IReadOnlyList<ElementsInput> models)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (models is null) throw new ArgumentNullException(nameof(models));

        var result = new Dictionary<(string ModelKey, string UniqueId), (IReadOnlyList<Guid>, IReadOnlyList<Guid>)>();
        foreach (var model in models)
        {
            var sets = SetsFor(config, model);
            if (sets.Count == 0) continue;

            foreach (var info in model.Elements)
            {
                var parameters = model.Parameters.For(info.UniqueId);
                List<Guid>? matched = null;
                List<Guid>? groups = null;
                foreach (var set in sets)
                {
                    if (!Matches(set, info, parameters)) continue;
                    (matched ??= []).Add(set.Id);
                    groups ??= [];
                    if (!groups.Contains(set.GroupId)) groups.Add(set.GroupId);
                }

                if (matched is not null) result[(model.ModelKey, info.UniqueId)] = (matched, groups!);
            }
        }

        return new SetMembership(result);
    }

    /// <summary>Наборы, которые смотрят на элементы модели: группа набора есть и модель входит в её источник.</summary>
    internal static List<SelectionSet> SetsFor(ClashConfiguration config, ElementsInput model)
    {
        var groups = config.Groups.Where(g => g.Source.Includes(model.Discipline, model.ModelKey)).Select(g => g.Id).ToHashSet();
        return config.Sets.Where(s => groups.Contains(s.GroupId)).ToList();
    }

    /// <summary>Id наборов элемента; пустой список, если элемент неизвестен или ни в один набор не попал.</summary>
    public IReadOnlyList<Guid> SetsOf(string modelKey, string uniqueId) =>
        _sets.TryGetValue((modelKey, uniqueId), out var entry) ? entry.Sets : NoSets;

    /// <summary>Id групп наборов элемента в порядке наборов; пустой список, если элемент ни в один набор не попал.</summary>
    public IReadOnlyList<Guid> GroupsOf(string modelKey, string uniqueId) =>
        _sets.TryGetValue((modelKey, uniqueId), out var entry) ? entry.Groups : NoSets;

    internal static bool Matches(SelectionSet set, ElementInfo info, IReadOnlyList<ParameterValue> parameters)
    {
        foreach (var group in set.AnyOf)
            if (MatchesAll(group, info, parameters)) return true;

        return false;
    }

    internal static bool MatchesAll(ConditionGroup group, ElementInfo info, IReadOnlyList<ParameterValue> parameters)
    {
        foreach (var condition in group.All)
            if (!condition.Matches(info, parameters)) return false;

        return true;
    }
}
