using System.Text.Json;
using Alexpivo394.Revit.Clash.Engine.Identity;
using Alexpivo394.Revit.Clash.Geometry;

namespace ToadClash.Bench;

/// <summary>Сравнение геометрии двух TCG одной модели; JSON выводится в stdout, ошибки — в stderr.</summary>
public static class IdentityCommand
{
    /// <summary>Исполняет identity --old &lt;tcg&gt; --new &lt;tcg&gt;. Код 0 — успех, 2 — ошибка входных данных.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                var name = args[index];
                if (name is not ("--old" or "--new")) throw new ArgumentException($"Неизвестный параметр '{name}'.");
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Не задан путь для {name}.");
                if (!options.TryAdd(name, args[index + 1])) throw new ArgumentException($"Повторный параметр {name}.");
            }
            ModelGeometry Read(string name)
            {
                if (!options.TryGetValue(name, out var path)) throw new ArgumentException($"Не задан параметр {name}.");
                using var stream = File.OpenRead(path);
                var model = ModelGeometryFile.Read(stream);
                if (!model.Header.Complete) error.WriteLine($"Предупреждение: {name} '{path}' — неполная выгрузка; отчёт относится только к доступной геометрии.");
                return model;
            }
            var report = ElementMatcher.Compare(Read("--old"), Read("--new"));
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            error.WriteLine($"identity: {exception.Message}");
            error.WriteLine("Использование: identity --old <tcg> --new <tcg>");
            return 2;
        }
    }
}
