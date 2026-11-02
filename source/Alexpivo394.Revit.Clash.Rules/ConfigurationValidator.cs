using System.Globalization;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Ошибки конфигурации, которые не дают её сохранить или прогнать. Тексты — для пользователя.</summary>
public static class ConfigurationValidator
{
    /// <summary>Нижний предел допуска клетки, мм: мельче не различает точная проверка треугольников.</summary>
    public const double MinToleranceMm = 0.05;

    public const double MaxToleranceMm = 1000;

    private static readonly StringComparer Text = StringComparer.OrdinalIgnoreCase;

    /// <param name="disciplines">Разделы моделей части: источник группы из другого раздела — ошибка.</param>
    /// <param name="modelKeys">Ключи моделей части: источник группы из модели не этой части — ошибка.</param>
    public static IReadOnlyList<string> Problems(ClashConfiguration config, IReadOnlyCollection<string> disciplines, IReadOnlyCollection<string> modelKeys)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (disciplines is null) throw new ArgumentNullException(nameof(disciplines));
        if (modelKeys is null) throw new ArgumentNullException(nameof(modelKeys));

        var known = new HashSet<string>(disciplines, Text);
        var models = new HashSet<string>(modelKeys, StringComparer.Ordinal);
        var problems = new List<string>();
        if (config.Groups.GroupBy(g => g.Id).Any(g => g.Count() > 1)) problems.Add("Две группы с одинаковым Id — пересоздайте одну из них");
        foreach (var duplicate in config.Sets.GroupBy(s => s.Id).Where(g => g.Count() > 1))
            problems.Add($"Два набора с одинаковым Id: «{duplicate.First().Name}» и «{duplicate.Skip(1).First().Name}»");

        // При дублях Id остальные проверки идут по первой группе с этим Id.
        var groups = config.Groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());

        foreach (var group in config.Groups)
        {
            var label = string.IsNullOrWhiteSpace(group.Name) ? "Группа без имени" : $"Группа «{group.Name.Trim()}»";
            if (string.IsNullOrWhiteSpace(group.Name)) problems.Add($"{label}: укажите имя");
            if (group.Source.IsEmpty) problems.Add($"{label}: не задан источник — разделы или модели, из которых берутся элементы");
            foreach (var discipline in group.Source.Disciplines.Where(d => !known.Contains(d)))
                problems.Add($"{label}: раздел «{discipline}» не встречается в моделях части");
            if (group.Source.ModelKeys.Any(k => !models.Contains(k)))
                problems.Add($"{label}: модель источника не входит в часть — выберите источник заново");
        }

        foreach (var duplicate in config.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name))
                     .GroupBy(g => g.Name.Trim().ToUpperInvariant()).Where(g => g.Count() > 1))
            problems.Add($"Две группы с именем «{duplicate.First().Name.Trim()}»");

        foreach (var set in config.Sets)
        {
            var groupName = groups.TryGetValue(set.GroupId, out var owner) ? owner.Name.Trim() : null;
            var label = string.IsNullOrWhiteSpace(set.Name) ? $"Набор без имени в группе «{groupName}»" : $"Набор «{set.Name}» ({groupName})";
            if (groupName is null)
            {
                problems.Add($"Набор «{set.Name}»: его группа удалена");
                continue;
            }

            if (string.IsNullOrWhiteSpace(set.Name)) problems.Add($"{label}: укажите имя");
            if (set.AnyOf.Count == 0 || set.AnyOf.Any(g => g.All.Count == 0))
            {
                problems.Add($"{label}: нет условий — набор отобрал бы все элементы группы или ни одного");
                continue;
            }

            foreach (var condition in set.AnyOf.SelectMany(g => g.All))
                if (ConditionProblem(condition) is { } problem) problems.Add($"{label}: {problem}");
        }

        foreach (var duplicate in config.Sets.Where(s => !string.IsNullOrWhiteSpace(s.Name) && groups.ContainsKey(s.GroupId))
                     .GroupBy(s => (s.GroupId, Name: s.Name.Trim().ToUpperInvariant()))
                     .Where(g => g.Count() > 1))
            problems.Add($"Два набора с именем «{duplicate.Last().Name.Trim()}» в группе «{groups[duplicate.Key.GroupId].Name.Trim()}»");

        var cells = new HashSet<(Guid, Guid)>();
        foreach (var cell in config.Matrix)
        {
            var nameA = groups.TryGetValue(cell.GroupA, out var a) ? a.Name.Trim() : null;
            var nameB = groups.TryGetValue(cell.GroupB, out var b) ? b.Name.Trim() : null;
            if (nameA is null || nameB is null)
            {
                problems.Add("Клетка матрицы ссылается на удалённую группу");
                continue;
            }

            var pair = $"«{nameA}» × «{nameB}»";
            if (cell.ToleranceMm < MinToleranceMm || cell.ToleranceMm > MaxToleranceMm)
                problems.Add($"Матрица {pair}: допуск — от {MinToleranceMm.ToString(CultureInfo.GetCultureInfo("ru-RU"))} до {MaxToleranceMm} мм");
            if (!cells.Add(cell.GroupA.CompareTo(cell.GroupB) <= 0 ? (cell.GroupA, cell.GroupB) : (cell.GroupB, cell.GroupA)))
                problems.Add($"Матрица {pair}: клетка задана дважды");
        }

        var ids = config.Sets.Select(s => s.Id).ToHashSet();
        foreach (var exclusion in config.Exclusions.Where(e => !ids.Contains(e.SetA) || !ids.Contains(e.SetB)))
            problems.Add("Одно из исключений ссылается на удалённый набор");

        return problems;
    }

    private static string? ConditionProblem(Condition condition)
    {
        var property = condition.Property;
        if (property.Kind == PropertyKind.Parameter && string.IsNullOrWhiteSpace(property.ParameterName) && property.SharedGuid is null)
            return "у условия не выбран параметр";

        switch (condition.Operator)
        {
            case ConditionOperator.Greater or ConditionOperator.Less:
                var value = (condition.Value ?? "").Trim().Replace(',', '.');
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number)
                    ? null
                    : $"«больше» и «меньше» сравнивают с числом, а не с «{condition.Value}»";
            case ConditionOperator.Equals or ConditionOperator.NotEquals or ConditionOperator.Contains or ConditionOperator.NotContains
                when string.IsNullOrWhiteSpace(condition.Value):
                return "у условия не задано значение";
            case ConditionOperator.OneOf when condition.Values is null || condition.Values.All(string.IsNullOrWhiteSpace):
                return "в условии «одно из» не выбрано ни одного значения";
            default:
                return null;
        }
    }
}
