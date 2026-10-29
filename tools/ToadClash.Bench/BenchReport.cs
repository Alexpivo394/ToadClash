using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadClash.Bench;

/// <summary>Результат замера. Имена свойств в JSON - camelCase, как в отчёте этапа 0.</summary>
public sealed record BenchReport(
    int Elements,
    long Triangles,
    long Candidates,
    int Hits,
    double BroadSeconds,
    double NarrowSeconds,
    double TotalSeconds,
    long PeakManagedMb,
    long PeakWorkingSetMb,
    double LoadSeconds)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>JSON отчёта.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    ///     Читает модели части из каталога (<c>*.tcg</c> с парным <c>*.tcp</c> и <c>config.json</c>), запускает движок и
    ///     замеряет время. Раздел берётся из <c>model-disciplines.json</c> по ModelKey заголовка: из корня и из каталогов
    ///     слепков. Без явной записи допустим ключ раздела или ключ вида «раздел-номер» из синтетики.
    ///     Если задан <paramref name="pairs"/>, туда пишется таблица найденных пар через табуляцию.
    /// </summary>
    public static BenchReport Run(string directory, int threads, TextWriter diagnostics, TextWriter? pairs = null)
    {
        var total = Stopwatch.StartNew();
        var config = ConfigurationJson.Deserialize(File.ReadAllText(Path.Combine(directory, "config.json")));
        var mapping = ReadMapping(directory);
        var disciplines = config.Groups.SelectMany(group => group.Source.Disciplines)

            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = EnumerateGeometry(directory).Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException("В каталоге нет готовых файлов геометрии .tcg.");
        var inputs = new ModelInput[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount }, i =>
        {
            using var geometryStream = File.OpenRead(files[i]);
            var geometry = ModelGeometryFile.Read(geometryStream);
            using var parametersStream = File.OpenRead(Path.ChangeExtension(files[i], ".tcp"));
            var parameters = ModelParametersFile.Read(parametersStream);
            var modelKey = geometry.Header.ModelKey;
            var separator = modelKey.LastIndexOf('-');
            var discipline = mapping.TryGetValue(modelKey, out var explicitDiscipline)
                ? explicitDiscipline
                : disciplines.Contains(modelKey) ? modelKey : separator > 0 ? modelKey[..separator] : modelKey;
            if (string.IsNullOrWhiteSpace(discipline) || !disciplines.Contains(discipline))
                throw new InvalidDataException($"Для модели '{modelKey}' не определён раздел из конфигурации. Укажите его в model-disciplines.json.");
            inputs[i] = new ModelInput(discipline, geometry, parameters);
        });
        var modelDisciplines = inputs.ToDictionary(input => input.Geometry.Header.ModelKey, input => input.Discipline, StringComparer.Ordinal);
        var load = total.Elapsed;

        var result = new ClashEngine(new EngineOptions(threads)).Run(inputs, config, CancellationToken.None);
        var totalSeconds = total.Elapsed;

        var intersecting = result.Hits.SelectMany(h => new[] { h.A, h.B }).Distinct().Count();
        diagnostics.WriteLine(
            $"Элементов в коллизиях: {intersecting} ({100.0 * intersecting / Math.Max(1, result.Stats.Elements):0.0}%), " +
            $"среднее треугольников на элемент: {(double)result.Stats.Triangles / Math.Max(1, result.Stats.Elements):0}");

        foreach (var group in result.Hits.GroupBy(h => DisciplinePair(modelDisciplines[h.A.ModelKey], modelDisciplines[h.B.ModelKey])).OrderByDescending(g => g.Count()).Take(12))
            diagnostics.WriteLine($"  {group.Key.Item1} x {group.Key.Item2}: {group.Count()}");

        if (pairs is not null) WritePairs(pairs, result.Hits, inputs);

        const long mb = 1024 * 1024;
        return new BenchReport(
            result.Stats.Elements,
            result.Stats.Triangles,
            result.Stats.CandidatePairs,
            result.Hits.Count,
            Math.Round(result.Stats.BroadPhase.TotalSeconds, 3),
            Math.Round(result.Stats.NarrowPhase.TotalSeconds, 3),
            Math.Round(totalSeconds.TotalSeconds, 3),
            result.Stats.PeakManagedBytes / mb,
            Process.GetCurrentProcess().PeakWorkingSet64 / mb,
            Math.Round(load.TotalSeconds, 3));
    }

    /// <summary>Объединяет все model-disciplines.json каталога и слепков; разные разделы одного ключа - ошибка.</summary>
    private static Dictionary<string, string> ReadMapping(string directory)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = EnumerateFolders(directory).Select(folder => Path.Combine(folder, "model-disciplines.json")).Where(File.Exists);
        foreach (var file in files)
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                ?? throw new InvalidDataException($"{file} должен содержать объект ключ модели → раздел.");
            foreach (var (key, discipline) in entries)
            {
                if (mapping.TryGetValue(key, out var existing) && !string.Equals(existing, discipline, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Для модели '{key}' заданы разные разделы: '{existing}' и '{discipline}'.");
                mapping[key] = discipline;
            }
        }

        return mapping;
    }

    private static void WritePairs(TextWriter output, IReadOnlyList<ClashHit> hits, ModelInput[] inputs)
    {
        var infos = inputs
            .SelectMany(input => input.Geometry.Elements.Select(element => (Ref: new ElementRef(input.Geometry.Header.ModelKey, element.Info.UniqueId), element.Info)))
            .ToDictionary(entry => entry.Ref, entry => entry.Info);
        output.WriteLine("modelA\tuniqueIdA\tcategoryA\tmodelB\tuniqueIdB\tcategoryB\telementIdA\telementIdB\tdepthMm\tx\ty\tz");
        foreach (var hit in hits.OrderBy(hit => hit.A).ThenBy(hit => hit.B))
        {
            var a = infos[hit.A];
            var b = infos[hit.B];
            output.WriteLine(string.Join("\t",
                hit.A.ModelKey, hit.A.UniqueId, a.Category, hit.B.ModelKey, hit.B.UniqueId, b.Category,
                a.ElementId.ToString(CultureInfo.InvariantCulture), b.ElementId.ToString(CultureInfo.InvariantCulture),
                hit.DepthMm.ToString("0.0", CultureInfo.InvariantCulture), hit.PointX.ToString("0", CultureInfo.InvariantCulture),
                hit.PointY.ToString("0", CultureInfo.InvariantCulture), hit.PointZ.ToString("0", CultureInfo.InvariantCulture)));
        }
    }

    private static IEnumerable<string> EnumerateGeometry(string directory) =>
        EnumerateFolders(directory).SelectMany(folder => Directory.EnumerateFiles(folder, "*.tcg"));

    /// <summary>Каталог и вложенные каталоги без незавершённых <c>.pending</c> и точек повторной обработки.</summary>
    private static IEnumerable<string> EnumerateFolders(string directory)
    {
        if (directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(".pending", StringComparison.OrdinalIgnoreCase)) yield break;
        yield return directory;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (child.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var folder in EnumerateFolders(child)) yield return folder;
        }
    }

    private static (string, string) DisciplinePair(string first, string second)
    {
        first = first.ToUpperInvariant();
        second = second.ToUpperInvariant();
        return string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
    }
}
