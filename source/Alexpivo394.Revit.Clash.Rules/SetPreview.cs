namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Элемент из образца предпросмотра набора.</summary>
public sealed record SetPreviewElement(string ModelKey, string UniqueId, long ElementId, string Category, string? Family, string? Type);

/// <summary>Сколько элементов попало в набор.</summary>
/// <param name="Total">Сколько всего элементов в источнике группы набора.</param>
/// <param name="ByAnyOf">Сколько элементов отобрала каждая группа условий «или» по отдельности.</param>
/// <param name="Sample">Первые попавшие элементы — показать, что именно отобрано.</param>
public sealed record SetPreviewResult(int Matched, int Total, IReadOnlyList<int> ByAnyOf, IReadOnlyList<SetPreviewElement> Sample);

/// <summary>Предпросмотр набора в редакторе: сколько элементов источника его группы он отбирает.</summary>
public static class SetPreview
{
    /// <exception cref="KeyNotFoundException">Набора нет в конфигурации.</exception>
    public static SetPreviewResult Count(ClashConfiguration config, Guid setId, IReadOnlyList<ElementsInput> models, int sample = 20)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (models is null) throw new ArgumentNullException(nameof(models));
        var set = config.Sets.FirstOrDefault(s => s.Id == setId) ?? throw new KeyNotFoundException($"Набора {setId} нет в конфигурации.");
        var group = config.Groups.FirstOrDefault(g => g.Id == set.GroupId);

        var matched = 0;
        var total = 0;
        var byAnyOf = new int[set.AnyOf.Count];
        var elements = new List<SetPreviewElement>(Math.Max(0, sample));
        foreach (var model in models)
        {
            if (group is null || !group.Source.Includes(model.Discipline, model.ModelKey)) continue;

            foreach (var info in model.Elements)
            {
                total++;
                var parameters = model.Parameters.For(info.UniqueId);
                var any = false;
                for (var i = 0; i < set.AnyOf.Count; i++)
                {
                    if (!SetMembership.MatchesAll(set.AnyOf[i], info, parameters)) continue;
                    byAnyOf[i]++;
                    any = true;
                }

                if (!any) continue;
                matched++;
                if (elements.Count < sample)
                    elements.Add(new SetPreviewElement(model.ModelKey, info.UniqueId, info.ElementId, info.Category, info.Family, info.Type));
            }
        }

        return new SetPreviewResult(matched, total, byAnyOf, elements);
    }
}
