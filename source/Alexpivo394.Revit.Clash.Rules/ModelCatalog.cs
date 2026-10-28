using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Категория в каталоге модели.</summary>
/// <param name="Key">Имя <c>BuiltInCategory</c>, например <c>OST_Walls</c>.</param>
/// <param name="Count">Сколько выгруженных элементов этой категории.</param>
public sealed record CatalogCategory(string Key, int Count);

/// <summary>Параметр в каталоге модели.</summary>
/// <param name="Name">Имя параметра, как оно встретилось первым.</param>
/// <param name="SharedGuid">GUID общего параметра; null — параметр опознаётся по имени.</param>
/// <param name="Count">У скольких элементов есть параметр.</param>
/// <param name="IsNumeric">Хотя бы одно значение — число: к параметру применимы «больше» и «меньше».</param>
/// <param name="Categories">Категории элементов, у которых встречен параметр, по алфавиту.</param>
public sealed record CatalogParameter(string Name, Guid? SharedGuid, int Count, bool IsNumeric, IReadOnlyList<string> Categories);

/// <summary>
///     Что встречается в выгруженных элементах модели: категории, параметры и значения свойств. Подсказки редактору
///     наборов. Учитываются только элементы с геометрией на виде выгрузки — только они попадают в наборы.
/// </summary>
public sealed record ModelCatalog(
    IReadOnlyList<CatalogCategory> Categories,
    IReadOnlyList<CatalogParameter> Parameters,
    IReadOnlyList<string> Families,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Worksets)
{
    private static readonly StringComparer Text = StringComparer.OrdinalIgnoreCase;

    /// <summary>Каталог по оглавлению выгрузки и параметрам её элементов.</summary>
    public static ModelCatalog Build(ModelIndex index, ModelParameters parameters)
    {
        if (index is null) throw new ArgumentNullException(nameof(index));
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));

        var builder = new Builder();
        foreach (var entry in index.Elements)
        {
            var info = entry.Info;
            builder.AddCategory(info.Category, 1);
            builder.AddValues(info.Family, info.Type, info.Level, info.Workset);
            foreach (var value in parameters.For(info.UniqueId))
                builder.AddParameter(value.Name, value.SharedGuid, 1, value.Number.HasValue, [info.Category]);
        }

        return builder.Build();
    }

    /// <summary>Каталог нескольких моделей: числа складываются, списки объединяются без учёта регистра.</summary>
    public static ModelCatalog Merge(IEnumerable<ModelCatalog> catalogs)
    {
        if (catalogs is null) throw new ArgumentNullException(nameof(catalogs));

        var builder = new Builder();
        foreach (var catalog in catalogs)
        {
            foreach (var category in catalog.Categories) builder.AddCategory(category.Key, category.Count);
            foreach (var parameter in catalog.Parameters)
                builder.AddParameter(parameter.Name, parameter.SharedGuid, parameter.Count, parameter.IsNumeric, parameter.Categories);
            builder.AddLists(catalog.Families, catalog.Types, catalog.Levels, catalog.Worksets);
        }

        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, int> _categories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ParameterAccumulator> _parameters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _families = new(Text);
        private readonly Dictionary<string, string> _types = new(Text);
        private readonly Dictionary<string, string> _levels = new(Text);
        private readonly Dictionary<string, string> _worksets = new(Text);

        public void AddCategory(string key, int count) => _categories[key] = _categories.TryGetValue(key, out var existing) ? existing + count : count;

        public void AddValues(string? family, string? type, string? level, string? workset)
        {
            Add(_families, family);
            Add(_types, type);
            Add(_levels, level);
            Add(_worksets, workset);
        }

        public void AddLists(IEnumerable<string> families, IEnumerable<string> types, IEnumerable<string> levels, IEnumerable<string> worksets)
        {
            foreach (var value in families) Add(_families, value);
            foreach (var value in types) Add(_types, value);
            foreach (var value in levels) Add(_levels, value);
            foreach (var value in worksets) Add(_worksets, value);
        }

        public void AddParameter(string name, Guid? sharedGuid, int count, bool isNumeric, IEnumerable<string> categories)
        {
            // Общий параметр опознаётся по GUID: под разными именами в разных семействах это один параметр.
            var key = sharedGuid is { } guid ? "g:" + guid.ToString("N") : "n:" + name.ToUpperInvariant();
            if (!_parameters.TryGetValue(key, out var accumulator))
                _parameters[key] = accumulator = new ParameterAccumulator(name, sharedGuid);
            accumulator.Count += count;
            accumulator.IsNumeric |= isNumeric;
            foreach (var category in categories) accumulator.Categories.Add(category);
        }

        public ModelCatalog Build() => new(
            _categories.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new CatalogCategory(c.Key, c.Value)).ToArray(),
            _parameters.Values.OrderBy(p => p.Name, Text)
                .Select(p => new CatalogParameter(p.Name, p.SharedGuid, p.Count, p.IsNumeric, p.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray()))
                .ToArray(),
            Sorted(_families), Sorted(_types), Sorted(_levels), Sorted(_worksets));

        private static void Add(Dictionary<string, string> values, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!values.ContainsKey(value!)) values[value!] = value!;
        }

        private static string[] Sorted(Dictionary<string, string> values) => values.Values.OrderBy(v => v, Text).ToArray();
    }

    private sealed class ParameterAccumulator(string name, Guid? sharedGuid)
    {
        public string Name { get; } = name;
        public Guid? SharedGuid { get; } = sharedGuid;
        public int Count { get; set; }
        public bool IsNumeric { get; set; }
        public HashSet<string> Categories { get; } = new(StringComparer.Ordinal);
    }
}
