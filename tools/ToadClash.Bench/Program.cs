using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using ToadClash.Bench;

if (args.Length == 0 || args[0] is not ("synth" or "run"))
{
    Console.Error.WriteLine("""
        Использование:
          synth --elements 200000 --models 12 --seed 1 [--profile standard|dense] --out <каталог>
          run --in <каталог> --threads 8
        """);
    return 2;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];

string Required(string name) =>
    options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Не задан параметр --{name}.");

int Number(string name, int fallback) => options.TryGetValue(name, out var value) ? int.Parse(value) : fallback;

if (args[0] == "synth")
{
    var directory = Required("out");
    Directory.CreateDirectory(directory);
    foreach (var stale in Directory.GetFiles(directory, "*.tc?")) File.Delete(stale);

    var elements = Number("elements", 200_000);
    var profile = options.TryGetValue("profile", out var name)
        ? Enum.Parse<SyntheticProfile>(name, ignoreCase: true)
        : SyntheticProfile.Standard;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    foreach (var (geometry, parameters, discipline) in SyntheticPart.Enumerate(elements, Number("models", 12), Number("seed", 1), profile))
    {
        var path = Path.Combine(directory, geometry.Header.ModelKey);
        using (var stream = File.Create(path + ".tcg")) ModelGeometryFile.Write(stream, geometry);
        using (var stream = File.Create(path + ".tcp")) ModelParametersFile.Write(stream, parameters);
        Console.Error.WriteLine(
            $"{geometry.Header.ModelKey} ({discipline}): {geometry.Elements.Count} элементов, " +
            $"{geometry.Elements.Sum(e => (long)e.Mesh.TriangleCount)} треугольников, {clock.Elapsed.TotalSeconds:0} с");
    }

    File.WriteAllText(Path.Combine(directory, "config.json"), ConfigurationJson.Serialize(SyntheticPart.CreateConfiguration()));
    return 0;
}

var report = BenchReport.Run(Required("in"), Number("threads", 0), Console.Error);
Console.WriteLine(report.ToJson());
return 0;
