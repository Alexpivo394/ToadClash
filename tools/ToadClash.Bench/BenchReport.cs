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

    private static string Prefix(string modelKey) => modelKey[..modelKey.LastIndexOf('-')];

    /// <summary>JSON отчёта.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    ///     Читает модели части из каталога (<c>*.tcg</c> с парным <c>*.tcp</c> и <c>config.json</c>), запускает движок и
    ///     замеряет время. <c>totalSeconds</c> включает чтение файлов; раздел модели - имя файла до последнего дефиса.
    /// </summary>
    public static BenchReport Run(string directory, int threads, TextWriter diagnostics)
    {
        var total = Stopwatch.StartNew();
        var config = ConfigurationJson.Deserialize(File.ReadAllText(Path.Combine(directory, "config.json")));
        var files = Directory.GetFiles(directory, "*.tcg").Order(StringComparer.Ordinal).ToArray();
        var inputs = new ModelInput[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount }, i =>
        {
            var name = Path.GetFileNameWithoutExtension(files[i]);
            using var geometryStream = File.OpenRead(files[i]);
            var geometry = ModelGeometryFile.Read(geometryStream);
            using var parametersStream = File.OpenRead(Path.ChangeExtension(files[i], ".tcp"));
            var parameters = ModelParametersFile.Read(parametersStream);
            inputs[i] = new ModelInput(name[..name.LastIndexOf('-')], geometry, parameters);
        });
        var load = total.Elapsed;

        var result = new ClashEngine(new EngineOptions(threads)).Run(inputs, config, CancellationToken.None);
        var totalSeconds = total.Elapsed;

        var intersecting = result.Hits.SelectMany(h => new[] { h.A, h.B }).Distinct().Count();
        diagnostics.WriteLine(
            $"Элементов в коллизиях: {intersecting} ({100.0 * intersecting / Math.Max(1, result.Stats.Elements):0.0}%), " +
            $"среднее треугольников на элемент: {(double)result.Stats.Triangles / Math.Max(1, result.Stats.Elements):0}");

        foreach (var group in result.Hits.GroupBy(h => (Prefix(h.A.ModelKey), Prefix(h.B.ModelKey))).OrderByDescending(g => g.Count()).Take(12))
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
}
