using System.Globalization;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Ошибки конфигурации, которые не дают её сохранить или прогнать. Тексты — для пользователя.</summary>
public static class ConfigurationValidator
{
    /// <summary>Нижний предел допуска клетки, мм: мельче не различает точная проверка треугольников.</summary>
    public const double MinToleranceMm = 0.05;

    public const double MaxToleranceMm = 1000;

    private static readonly StringComparer Text = StringComparer.OrdinalIgnoreCase;

    /// <param name="disciplines">Разделы моделей части: наборы и клетки матрицы других разделов — ошибка.</param>
    public static IReadOnlyList<string> Problems(ClashConfiguration config, IReadOnlyCollection<string> disciplines)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (disciplines is null) throw new ArgumentNullException(nameof(disciplines));

        var known = new HashSet<string>(disciplines, Text);
        var problems = new List<string>();

        foreach (var set in config.Sets)
        {
            var label = string.IsNullOrWhiteSpace(set.Name) ? $"Набор без имени в разделе «{set.Discipline}»" : $"Набор «{set.Name}» ({set.Discipline})";
            if (string.IsNullOrWhiteSpace(set.Name)) problems.Add($"{label}: укажите имя");
            if (!known.Contains(set.Discipline)) problems.Add($"{label}: раздел «{set.Discipline}» не встречается в моделях части");
            if (set.AnyOf.Count == 0 || set.AnyOf.Any(g => g.All.Count == 0))
            {
                problems.Add($"{label}: нет условий — набор отобрал бы все элементы раздела или ни одного");
                continue;
            }

            foreach (var condition in set.AnyOf.SelectMany(g => g.All))
                if (ConditionProblem(condition) is { } problem) problems.Add($"{label}: {problem}");
        }

        foreach (var duplicate in config.Sets.Where(s => !string.IsNullOrWhiteSpace(s.Name))
                     .GroupBy(s => (Discipline: s.Discipline.ToUpperInvariant(), Name: s.Name.Trim().ToUpperInvariant()))
                     .Where(g => g.Count() > 1))
            problems.Add($"Два набора с именем «{duplicate.Last().Name.Trim()}» в разделе «{duplicate.First().Discipline}»");

        var cells = new HashSet<(string, string)>();
        foreach (var cell in config.Matrix)
        {
            var pair = $"«{cell.DisciplineA}» × «{cell.DisciplineB}»";
            foreach (var discipline in new[] { cell.DisciplineA, cell.DisciplineB }.Distinct(Text).Where(d => !known.Contains(d)))
                problems.Add($"Матрица {pair}: раздел «{discipline}» не встречается в моделях части");
            if (cell.ToleranceMm < MinToleranceMm || cell.ToleranceMm > MaxToleranceMm)
                problems.Add($"Матрица {pair}: допуск — от {MinToleranceMm.ToString(CultureInfo.GetCultureInfo("ru-RU"))} до {MaxToleranceMm} мм");

            var a = cell.DisciplineA.ToUpperInvariant();
            var b = cell.DisciplineB.ToUpperInvariant();
            if (!cells.Add(string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a))) problems.Add($"Матрица {pair}: клетка задана дважды");
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
            default:
                return null;
        }
    }
}
