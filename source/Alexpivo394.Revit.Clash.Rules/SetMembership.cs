using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>
///     Для каждого элемента - Id наборов его раздела, в которые он попал. Элемент без наборов в проверке не участвует.
/// </summary>
public sealed class SetMembership
{
    private static readonly IReadOnlyList<Guid> NoSets = [];

    private readonly Dictionary<(string ModelKey, string UniqueId), IReadOnlyList<Guid>> _sets;

    private SetMembership(Dictionary<(string ModelKey, string UniqueId), IReadOnlyList<Guid>> sets) => _sets = sets;

    /// <summary>Вычисляет принадлежность элементов наборам; учитываются только наборы раздела модели.</summary>
    public static SetMembership Evaluate(ClashConfiguration config, IReadOnlyList<ModelInput> models)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (models is null) throw new ArgumentNullException(nameof(models));

        var result = new Dictionary<(string ModelKey, string UniqueId), IReadOnlyList<Guid>>();
        foreach (var model in models)
        {
            var sets = config.Sets
                .Where(s => string.Equals(s.Discipline, model.Discipline, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sets.Count == 0) continue;

            var modelKey = model.Geometry.Header.ModelKey;
            foreach (var element in model.Geometry.Elements)
            {
                var parameters = model.Parameters.For(element.Info.UniqueId);
                List<Guid>? matched = null;
                foreach (var set in sets)
                {
                    if (!Matches(set, element.Info, parameters)) continue;
                    (matched ??= []).Add(set.Id);
                }

                if (matched is not null) result[(modelKey, element.Info.UniqueId)] = matched;
            }
        }

        return new SetMembership(result);
    }

    /// <summary>Id наборов элемента; пустой список, если элемент неизвестен или ни в один набор не попал.</summary>
    public IReadOnlyList<Guid> SetsOf(string modelKey, string uniqueId) =>
        _sets.TryGetValue((modelKey, uniqueId), out var ids) ? ids : NoSets;

    private static bool Matches(SelectionSet set, ElementInfo info, IReadOnlyList<ParameterValue> parameters)
    {
        foreach (var group in set.AnyOf)
        {
            var all = true;
            foreach (var condition in group.All)
            {
                if (condition.Matches(info, parameters)) continue;
                all = false;
                break;
            }

            if (all) return true;
        }

        return false;
    }
}
