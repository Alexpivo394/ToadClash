using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;
using ToadClash.Bench;

namespace Alexpivo394.Revit.Clash.Tests.Bench;

public sealed class BenchReportTests
{
    [Test]
    [Arguments("OV")]
    [Arguments("OV-1")]
    public void Run_KnownDisciplineKey_DoesNotRequireMapping(string key)
    {
        WithEmptyModel(key, directory => BenchReport.Run(directory, 1, TextWriter.Null).Elements.ShouldBe(0));
    }

    [Test]
    public void Run_UnknownDiscipline_FailsInsteadOfSilentlySkippingModel()
    {
        WithEmptyModel("unknown", directory =>
            Should.Throw<AggregateException>(() => BenchReport.Run(directory, 1, TextWriter.Null))
                .Flatten().InnerExceptions.ShouldContain(error => error is InvalidDataException && error.Message.Contains("model-disciplines.json")));
    }

    private static void WithEmptyModel(string key, Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "toadclash-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.json"), ConfigurationJson.Serialize(
                new ClashConfiguration([TestScenes.CategorySet("OV", "OST_PipeCurves")], [new MatrixCell("OV", "OV", true)], [])));
            var model = new ModelGeometry(new ModelHeader(key, "", 2022, DateTimeOffset.UtcNow, true, "3D", 0, 0, 0), [], []);
            using (var stream = File.Create(Path.Combine(directory, "arbitrary.tcg"))) ModelGeometryFile.Write(stream, model);
            using (var stream = File.Create(Path.Combine(directory, "arbitrary.tcp")))
                ModelParametersFile.Write(stream, new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()));
            action(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Run_MappingInsideSnapshotDirectory_IsUsed()
    {
        WithOverlappingSnapshots(writeRootMapping: false, directory =>
            BenchReport.Run(directory, 1, TextWriter.Null).Hits.ShouldBe(1));
    }

    [Test]
    public void Run_PairsWriter_WritesOneRowPerHit()
    {
        WithOverlappingSnapshots(writeRootMapping: true, directory =>
        {
            using var pairs = new StringWriter();
            BenchReport.Run(directory, 1, TextWriter.Null, pairs);

            var lines = pairs.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(2);
            lines[0].ShouldStartWith("modelA\t");
            lines[1].Split('\t')[..6].ShouldBe(["pipeModel", "pipe", "OST_PipeCurves", "wallModel", "wall", "OST_Walls"]);
        });
    }

    private static void WithOverlappingSnapshots(bool writeRootMapping, Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "toadclash-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.json"), ConfigurationJson.Serialize(new ClashConfiguration(
                [TestScenes.CategorySet("OV", "OST_PipeCurves"), TestScenes.CategorySet("AR", "OST_Walls")],
                [new MatrixCell("OV", "AR", true)], [])));
            if (writeRootMapping)
                File.WriteAllText(Path.Combine(directory, "model-disciplines.json"), """{"pipeModel":"OV","wallModel":"AR"}""");
            WriteSnapshot("pipe.snapshot", "pipeModel", "OV", "pipe", "OST_PipeCurves", 0);
            WriteSnapshot("wall.snapshot", "wallModel", "AR", "wall", "OST_Walls", 500);
            action(directory);

            void WriteSnapshot(string folder, string key, string discipline, string uniqueId, string category, float offset)
            {
                var mesh = TestScenes.Box(new Vector3(offset, 0, 0), new Vector3(offset + 1000, 1000, 1000));
                var input = TestScenes.Model(key, discipline, (0, 0, 0), [new(uniqueId, category, mesh)]);
                var snapshot = Path.Combine(directory, folder);
                ModelSnapshotFiles.Write(snapshot, input.Geometry, input.Parameters);
                if (!writeRootMapping)
                    File.WriteAllText(Path.Combine(snapshot, "model-disciplines.json"), "{\"" + key + "\":\"" + discipline + "\"}");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Run_ExplicitModelDisciplines_UsesHeaderKeysAndReportsHit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "toadclash-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ClashConfiguration(
                [TestScenes.CategorySet("OV", "OST_PipeCurves"), TestScenes.CategorySet("AR", "OST_Walls")],
                [new MatrixCell("OV", "AR", true)], []);
            File.WriteAllText(Path.Combine(directory, "config.json"), ConfigurationJson.Serialize(configuration));
            File.WriteAllText(Path.Combine(directory, "model-disciplines.json"), """{"pipeModel":"OV","wallModel":"AR","zPipeModel":"OV"}""");
            WriteModel("arbitrary", "pipeModel", "OV", "OST_PipeCurves", 0);
            WriteModel("another", "wallModel", "AR", "OST_Walls", 500);
            WriteModel("third", "zPipeModel", "OV", "OST_PipeCurves", 1000);
            var incomplete = Path.Combine(directory, ".interrupted.pending");
            Directory.CreateDirectory(incomplete);
            File.WriteAllText(Path.Combine(incomplete, "model.tcg"), "incomplete header");

            using var diagnostics = new StringWriter();
            var report = BenchReport.Run(directory, 1, diagnostics);

            report.Hits.ShouldBe(2);
            diagnostics.ToString().ShouldContain("AR x OV: 2");

            void WriteModel(string file, string key, string discipline, string category, float offset)
            {
                var mesh = TestScenes.Box(new Vector3(offset, 0, 0), new Vector3(offset + 1000, 1000, 1000));
                var input = TestScenes.Model(key, discipline, (0, 0, 0), [new("element", category, mesh)]);
                ModelSnapshotFiles.Write(Path.Combine(directory, file), input.Geometry, input.Parameters);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
