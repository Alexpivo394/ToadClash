using System.Diagnostics;
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
    ///     замеряет время. Раздел берётся из <c>model-disciplines.json</c> по ModelKey заголовка.
    ///     Без явной записи допустим ключ раздела или ключ вида «раздел-номер» из синтетики.
    /// </summary>
    public static BenchReport Run(string directory, int threads, TextWriter diagnostics)
    {
        var total = Stopwatch.StartNew();
        var config = ConfigurationJson.Deserialize(File.ReadAllText(Path.Combine(directory, "config.json")));
        var mappingPath = Path.Combine(directory, "model-disciplines.json");
        var mapping = File.Exists(mappingPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mappingPath))
                ?? throw new InvalidDataException("model-disciplines.json должен содержать объект ключ модели → раздел.")
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var disciplines = config.Sets.Select(set => set.Discipline)
            .Concat(config.Matrix.SelectMany(cell => new[] { cell.DisciplineA, cell.DisciplineB }))
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

    private static IEnumerable<string> EnumerateGeometry(string directory)
    {
        if (directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(".pending", StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*.tcg")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (child.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in EnumerateGeometry(child)) yield return file;
        }
    }

    private static (string, string) DisciplinePair(string first, string second)
    {
        first = first.ToUpperInvariant();
        second = second.ToUpperInvariant();
        return string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
    }
}
