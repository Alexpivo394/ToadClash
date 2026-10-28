namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Элемент из образца предпросмотра набора.</summary>
public sealed record SetPreviewElement(string ModelKey, string UniqueId, long ElementId, string Category, string? Family, string? Type);

/// <summary>Сколько элементов попало в набор.</summary>
/// <param name="Total">Сколько всего элементов в моделях раздела набора.</param>
/// <param name="Sample">Первые попавшие элементы — показать, что именно отобрано.</param>
public sealed record SetPreviewResult(int Matched, int Total, IReadOnlyList<SetPreviewElement> Sample);

/// <summary>Предпросмотр набора в редакторе: сколько элементов его раздела он отбирает.</summary>
public static class SetPreview
{
    public static SetPreviewResult Count(SelectionSet set, IReadOnlyList<ElementsInput> models, int sample = 20)
    {
        if (set is null) throw new ArgumentNullException(nameof(set));
        if (models is null) throw new ArgumentNullException(nameof(models));

        var matched = 0;
        var total = 0;
        var elements = new List<SetPreviewElement>(Math.Max(0, sample));
        foreach (var model in models)
        {
            if (!string.Equals(model.Discipline, set.Discipline, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var info in model.Elements)
            {
                total++;
                if (!SetMembership.Matches(set, info, model.Parameters.For(info.UniqueId))) continue;

                matched++;
                if (elements.Count < sample)
                    elements.Add(new SetPreviewElement(model.ModelKey, info.UniqueId, info.ElementId, info.Category, info.Family, info.Type));
            }
        }

        return new SetPreviewResult(matched, total, elements);
    }
}
