using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>
///     JSON-представление <see cref="ClashConfiguration"/>: camelCase, перечисления строками. Читается и прежний формат без
///     групп (наборы и клетки по разделам) — он переводится на группы разделов.
/// </summary>
public static class ConfigurationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Сериализует конфигурацию в JSON.</summary>
    public static string Serialize(ClashConfiguration c)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        return JsonSerializer.Serialize(c, Options);
    }

    /// <summary>Разбирает конфигурацию из JSON.</summary>
    /// <exception cref="JsonException">JSON некорректен или пуст.</exception>
    public static ClashConfiguration Deserialize(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Конфигурация пуста.");
        if (HasProperty(document.RootElement, "groups"))
            return Checked(document.RootElement.Deserialize<ClashConfiguration>(Options) ?? throw new JsonException("Конфигурация пуста."));

        var legacy = document.RootElement.Deserialize<Legacy>(Options) ?? throw new JsonException("Конфигурация пуста.");
        var sets = NotNull(legacy.Sets, "sets");
        foreach (var set in sets) CheckConditions(set.AnyOf);
        return Checked(ConfigurationDefaults.FromDisciplines(sets, NotNull(legacy.Matrix, "matrix"), NotNull(legacy.Exclusions, "exclusions"),
            legacy.IgnoreConnectedMep));
    }

    // JSON допускает null там, где модель конфигурации его не ждёт: такие места — ошибка разбора, а не NullReferenceException позже.
    private static ClashConfiguration Checked(ClashConfiguration config)
    {
        foreach (var group in NotNull(config.Groups, "groups"))
        {
            var source = group.Source ?? throw Empty("source");
            NotNull(source.Disciplines, "disciplines");
            NotNull(source.ModelKeys, "modelKeys");
        }

        foreach (var set in NotNull(config.Sets, "sets")) CheckConditions(set.AnyOf);
        NotNull(config.Matrix, "matrix");
        NotNull(config.Exclusions, "exclusions");
        return config;
    }

    private static void CheckConditions(IReadOnlyList<ConditionGroup>? anyOf)
    {
        foreach (var group in NotNull(anyOf, "anyOf"))
        foreach (var condition in NotNull(group.All, "all"))
            if (condition.Property is null) throw Empty("property");
    }

    private static IReadOnlyList<T> NotNull<T>(IReadOnlyList<T>? list, string name) where T : class =>
        list is null || list.Any(item => item is null) ? throw Empty(name) : list;

    private static JsonException Empty(string name) => new($"В конфигурации пустой список «{name}»");

    /// <summary>JSON части конфигурации — для сравнения записей со списками по содержимому.</summary>
    internal static string Canonical<T>(T item) => JsonSerializer.Serialize(item, Options);

    private static bool HasProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private sealed record Legacy(
        IReadOnlyList<DisciplineSet>? Sets,
        IReadOnlyList<DisciplineCell>? Matrix,
        IReadOnlyList<SetPairExclusion>? Exclusions,
        bool IgnoreConnectedMep = true);
}
