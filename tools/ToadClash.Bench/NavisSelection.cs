using System.Globalization;
using System.Xml.Linq;
using Alexpivo394.Revit.Clash.Geometry;

namespace ToadClash.Bench;

/// <summary>Элемент выгрузки в терминах свойств Navisworks: файл источника - ключ модели.</summary>
public sealed record NavisItem(string ModelKey, ElementInfo Info, IReadOnlyList<ParameterValue> Parameters);

/// <summary>Поисковый набор Navisworks: OR условий-групп, внутри группы И.</summary>
public sealed class NavisSet
{
    private readonly IReadOnlyList<IReadOnlyList<NavisCondition>> _groups;

    internal NavisSet(string path, IReadOnlyList<IReadOnlyList<NavisCondition>> groups)
    {
        Path = path;
        _groups = groups;
    }

    /// <summary>Путь в дереве наборов через «/», без префикса lcop_selection_set_tree.</summary>
    public string Path { get; }

    /// <summary>Входит ли элемент в набор.</summary>
    public bool Matches(NavisItem item) => _groups.Any(group => group.All(condition => condition.Matches(item)));
}

/// <summary>
///     Разовый перенос поисковых наборов из XML настроек Navisworks для сравнения этапа 0; не импортёр продукта.
///     Флаги условия: 16 - без учёта регистра, 32 - отрицание, 64 - начало новой OR-группы.
///     Условие по отсутствующему свойству ложно (до отрицания).
/// </summary>
public sealed class NavisSelection
{
    private const string TreePrefix = "lcop_selection_set_tree";
    private readonly List<NavisSet> _sets;

    private NavisSelection(List<NavisSet> sets) => _sets = sets;

    /// <summary>Все наборы в порядке дерева.</summary>
    public IReadOnlyList<NavisSet> Sets => _sets;

    /// <summary>Разбирает элемент selectionsets.</summary>
    public static NavisSelection Parse(XElement selectionSets)
    {
        var sets = new List<NavisSet>();
        Walk(selectionSets, "");
        return new NavisSelection(sets);

        void Walk(XElement node, string prefix)
        {
            foreach (var child in node.Elements())
            {
                var path = prefix + (string?)child.Attribute("name");
                if (child.Name == "viewfolder") Walk(child, path + "/");
                else if (child.Name == "selectionset") sets.Add(ParseSet(child, path));
            }
        }
    }

    /// <summary>Наборы локатора: набор по полному пути или все наборы папки.</summary>
    public IReadOnlyList<NavisSet> Resolve(string locator)
    {
        var path = locator.StartsWith(TreePrefix + "/", StringComparison.Ordinal) ? locator[(TreePrefix.Length + 1)..] : locator;
        var exact = _sets.Where(set => set.Path == path).ToArray();
        return exact.Length > 0 ? exact : _sets.Where(set => set.Path.StartsWith(path + "/", StringComparison.Ordinal)).ToArray();
    }

    private static NavisSet ParseSet(XElement element, string path)
    {
        var conditions = element.Element("findspec")?.Element("conditions")?.Elements("condition")
            ?? throw new InvalidDataException($"Набор {path} не поисковый: явные выборки не поддерживаются.");
        var groups = new List<List<NavisCondition>>();
        foreach (var condition in conditions)
        {
            var parsed = NavisCondition.Parse(condition);
            if (groups.Count == 0 || parsed.StartsGroup) groups.Add([]);
            groups[^1].Add(parsed);
        }

        return new NavisSet(path, groups);
    }
}

/// <summary>Условие поискового набора.</summary>
internal sealed record NavisCondition(string Test, int Flags, string Property, string DisplayName, string DataType, string Value)
{
    private const double FeetToMillimeters = 304.8;

    public bool StartsGroup => (Flags & 64) != 0;

    private bool IgnoreCase => (Flags & 16) != 0;

    private bool Negated => (Flags & 32) != 0;

    public static NavisCondition Parse(XElement condition)
    {
        var name = condition.Element("property")?.Element("name") ?? throw new InvalidDataException("Условие без свойства.");
        var data = condition.Element("value")?.Element("data") ?? throw new InvalidDataException("Условие без значения.");
        return new NavisCondition((string)condition.Attribute("test")!, (int)condition.Attribute("flags")!,
            (string)name.Attribute("internal")!, name.Value, (string)data.Attribute("type")!, data.Value);
    }

    public bool Matches(NavisItem item)
    {
        var (text, number) = Read(item);
        var result = text is not null && Test switch
        {
            "equals" => Equal(text, number),
            "not_equals" => !Equal(text, number),
            "contains" => text.IndexOf(Value, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0,
            "greater_than" => number is { } value && value > Number(),
            "less_than" => number is { } value && value < Number(),
            _ => throw new InvalidDataException($"Условие {Test} не поддерживается.")
        };
        return result != Negated;
    }

    private bool Equal(string text, double? number) => DataType == "linear"
        ? number is { } value && Math.Abs(value - Number()) < 1e-6
        : string.Equals(text, Value, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private double Number()
    {
        var value = double.Parse(Value, CultureInfo.InvariantCulture);
        return DataType == "linear" ? value * FeetToMillimeters : value;
    }

    /// <summary>Встроенные свойства Navisworks сопоставлены с полями выгрузки; остальные ищутся по имени параметра.</summary>
    private (string? Text, double? Number) Read(NavisItem item) => Property switch
    {
        "LcOaNodeSourceFile" => (item.ModelKey, null),
        "LcRevitPropertyElementFamily" => (item.Info.Family, null),
        // Имя узла элемента Revit в дереве Navisworks - имя типа (smarttag «Элемент Имя» в отчётах).
        "LcRevitPropertyElementType" or "LcOaSceneBaseUserName" => (item.Info.Type, null),
        "lcldrevit_parameter_-1002053" => (item.Info.Workset, null),
        "LcRevitPropertyElementCategory" => Parameter(item, "Категория"),
        _ => Parameter(item, DisplayName)
    };

    private static (string? Text, double? Number) Parameter(NavisItem item, string name)
    {
        foreach (var parameter in item.Parameters)
        {
            if (parameter.Name == name) return (parameter.Text ?? parameter.Number?.ToString(CultureInfo.InvariantCulture), parameter.Number);
        }

        return (null, null);
    }
}
