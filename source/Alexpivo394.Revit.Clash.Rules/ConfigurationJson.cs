using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>JSON-представление <see cref="ClashConfiguration"/>: camelCase, перечисления строками.</summary>
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
        return JsonSerializer.Deserialize<ClashConfiguration>(json, Options)
               ?? throw new JsonException("Конфигурация пуста.");
    }
}
