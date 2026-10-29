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
            return document.RootElement.Deserialize<ClashConfiguration>(Options) ?? throw new JsonException("Конфигурация пуста.");

        var legacy = document.RootElement.Deserialize<Legacy>(Options) ?? throw new JsonException("Конфигурация пуста.");
        if (legacy.Sets is null || legacy.Matrix is null || legacy.Exclusions is null) throw new JsonException("В конфигурации нет списков наборов, матрицы или исключений.");
        return ConfigurationDefaults.FromDisciplines(legacy.Sets, legacy.Matrix, legacy.Exclusions, legacy.IgnoreConnectedMep);
    }

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
