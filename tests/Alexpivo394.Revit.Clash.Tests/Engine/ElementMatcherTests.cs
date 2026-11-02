using System.Numerics;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Engine.Identity;
using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;
using ToadClash.Bench;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class ElementMatcherTests
{
    private static ElementGeometry Element(string id, float center = 0, float size = 100, string type = "T") =>
        new(new(id, 1, "OST_PipeCurves", "F", type, null, null, []), new([], []),
            new(new Vector3(center - size / 2, 0, 0), new Vector3(center + size / 2, 100, 100)), 0);

    private static ModelGeometry Model(params ElementGeometry[] elements) =>
        new(new("model", "", 2022, DateTimeOffset.UnixEpoch, true, "3D", 0, 0, 0), elements, ["inventory-only"]);

    [Test]
    public void SameIds_AllKept()
    {
        var report = ElementMatcher.Compare(Model(Element("a")), Model(Element("a", 900, type: "changed")));
        report.Kept.ShouldBe(1);
        report.Removed.ShouldBe(0);
        report.Added.ShouldBe(0);
        report.FallbackMatches.ShouldBeEmpty();
    }

    [Test]
    public void RecreatedPipeSamePlace_FallbackMatched()
    {
        var report = ElementMatcher.Compare(Model(Element("old")), Model(Element("new")));
        report.FallbackMatches.ShouldBe([new IdentityMatch("old", "new")]);
        report.Kept.ShouldBe(0);
        report.Removed.ShouldBe(0);
        report.Added.ShouldBe(0);
        JsonSerializer.Serialize(report).ShouldContain("\"OldId\":\"old\"");
    }

    [Test]
    public void DifferentType_NotMatched() =>
        ElementMatcher.Compare(Model(Element("old")), Model(Element("new", type: "other")))
            .FallbackMatches.ShouldBeEmpty();

    [Test]
    public void TwoCandidates_NearestWins()
    {
        var report = ElementMatcher.Compare(Model(Element("old", 0)), Model(Element("far", 40), Element("near", 5)));
        report.FallbackMatches.ShouldBe([new IdentityMatch("old", "near")]);
        report.Added.ShouldBe(1);
    }

    [Test]
    public void GreedyUsesGlobalDistanceThenOrdinalIdsRegardlessOfInputOrder()
    {
        var report = ElementMatcher.Compare(Model(Element("b", 10), Element("a", 0)),
            Model(Element("y", 0), Element("x", 0)));
        report.FallbackMatches.ShouldBe([new IdentityMatch("a", "x"), new IdentityMatch("b", "y")]);
    }

    [Test]
    public void GlobalCentersUseDoubleAndBothOrigins()
    {
        var oldModel = Model(Element("old"));
        var newModel = Model(Element("new", -1000));
        oldModel = new(oldModel.Header with { OriginX = 1e12 }, oldModel.Elements, []);
        newModel = new(newModel.Header with { OriginX = 1e12 + 1051 }, newModel.Elements, []);
        ElementMatcher.Compare(oldModel, newModel).FallbackMatches.ShouldBeEmpty();
        newModel = new(newModel.Header with { OriginX = 1e12 + 1050 }, newModel.Elements, []);
        ElementMatcher.Compare(oldModel, newModel).FallbackMatches.Count.ShouldBe(1);
    }

    [Test]
    public void DimensionsMustDifferStrictlyLessThanOnePercentOfOldSize()
    {
        ElementMatcher.Compare(Model(Element("a")), Model(Element("b", size: 101))).FallbackMatches.ShouldBeEmpty();
        ElementMatcher.Compare(Model(Element("a")), Model(Element("b", size: 100.9f))).FallbackMatches.Count.ShouldBe(1);
        ElementMatcher.Compare(Model(Element("a", size: 0)), Model(Element("b", size: 0))).FallbackMatches.Count.ShouldBe(1);
        ElementMatcher.Compare(Model(Element("a", size: 0)), Model(Element("b", size: 0.1f))).FallbackMatches.ShouldBeEmpty();
    }

    [Test]
    public void RejectsDifferentModelKeysAndDuplicateIds()
    {
        var model = Model(Element("a"));
        Should.Throw<ArgumentException>(() => ElementMatcher.Compare(model,
            new(model.Header with { ModelKey = "other" }, model.Elements, []))).Message.ShouldContain("ModelKey");
        Should.Throw<ArgumentException>(() => ElementMatcher.Compare(Model(Element("a"), Element("a")), model));
    }

    [Test]
    public void RejectsInvalidToleranceAndExtremeCoordinates()
    {
        foreach (var tolerance in new[] { -1d, double.NaN, double.PositiveInfinity })
            Should.Throw<ArgumentOutOfRangeException>(() => ElementMatcher.Compare(Model(), Model(), tolerance));
        var model = Model(Element("a"));
        Should.Throw<ArgumentException>(() => ElementMatcher.Compare(
            new(model.Header with { OriginX = 1e30 }, model.Elements, []), Model(Element("b"))));
        ElementMatcher.Compare(Model(Element("a")), Model(Element("b")), 0).FallbackMatches.Count.ShouldBe(1);
    }

    [Test]
    public void Sparse200000ElementsMatch()
    {
        var oldElements = Enumerable.Range(0, 200_000).Select(index => Element($"old{index}", index * 200)).ToArray();
        var newElements = Enumerable.Range(0, 200_000).Select(index => Element($"new{index}", index * 200)).ToArray();
        ElementMatcher.Compare(Model(oldElements), Model(newElements)).FallbackMatches.Count.ShouldBe(200_000);
    }

    [Test]
    public void ExactIdsAreNeverFallbackCandidatesAndInventoryIsIgnored()
    {
        var report = ElementMatcher.Compare(Model(Element("kept"), Element("old", 500)),
            Model(Element("kept", 500), Element("new")));
        report.Kept.ShouldBe(1);
        report.Removed.ShouldBe(1);
        report.Added.ShouldBe(1);
        report.FallbackMatches.ShouldBeEmpty();
        ElementMatcher.Compare(Model(), Model()).Kept.ShouldBe(0);
    }

    [Test]
    public void CategoryAndFamilyMustMatchOrdinally()
    {
        var original = Element("new");
        foreach (var info in new[] { original.Info with { Category = "OST_Walls" },
                     original.Info with { Family = "f" }, original.Info with { Family = null } })
            ElementMatcher.Compare(Model(Element("old")), Model(original with { Info = info }))
                .FallbackMatches.ShouldBeEmpty();
    }

    [Test]
    public void NeighborBucketsIncludeNegativeCoordinatesButUseEuclideanDistance()
    {
        ElementMatcher.Compare(Model(Element("old", -1)), Model(Element("new", 49))).FallbackMatches.Count.ShouldBe(1);
        var diagonal = Element("new", 40);
        diagonal = diagonal with { Bounds = new(diagonal.Bounds.Min + new Vector3(0, 40, 0),
            diagonal.Bounds.Max + new Vector3(0, 40, 0)) };
        ElementMatcher.Compare(Model(Element("old")), Model(diagonal)).FallbackMatches.ShouldBeEmpty();
    }

    [Test]
    public void DenseData_ReturnsTruncatedWithoutMatches()
    {
        // Больше 2·10⁶ кандидатов: пересозданные одинаковые элементы в одной точке.
        var oldElements = Enumerable.Range(0, 1500).Select(index => Element($"old{index}")).Append(Element("kept")).ToArray();
        var newElements = Enumerable.Range(0, 1500).Select(index => Element($"new{index}")).Append(Element("kept")).ToArray();
        var report = ElementMatcher.Compare(Model(oldElements), Model(newElements));

        report.Truncated.ShouldBeTrue();
        report.FallbackMatches.ShouldBeEmpty();
        report.Kept.ShouldBe(1);
        report.Removed.ShouldBe(1500);
        report.Added.ShouldBe(1500);
    }

    [Test]
    public void TooManyComparisons_ReturnsTruncated()
    {
        // Больше 10⁷ сравнений, но размеры не совпадают — кандидатов нет.
        var oldElements = Enumerable.Range(0, 3200).Select(index => Element($"old{index}")).ToArray();
        var newElements = Enumerable.Range(0, 3200).Select(index => Element($"new{index}", size: 120)).ToArray();
        var report = ElementMatcher.Compare(Model(oldElements), Model(newElements));

        report.Truncated.ShouldBeTrue();
        report.Removed.ShouldBe(3200);
        report.Added.ShouldBe(3200);
    }

    [Test]
    public void IdentityCliReportsJsonAndUsefulErrors()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = File.Create(path)) ModelGeometryFile.Write(stream, Model(Element("a")));
            var output = new StringWriter();
            var error = new StringWriter();
            IdentityCommand.Run(["--old", path, "--new", path], output, error).ShouldBe(0);
            using var json = JsonDocument.Parse(output.ToString());
            json.RootElement.GetProperty("Kept").GetInt32().ShouldBe(1);
            IdentityCommand.Run(["--old"], output, error).ShouldBe(2);
            error.ToString().ShouldContain("--old");
            IdentityCommand.Run(["--old", path + ".missing", "--new", path], output, error).ShouldBe(2);
            IdentityCommand.Run(["--old", path, "--old", path], output, error).ShouldBe(2);
            IdentityCommand.Run(["--unknown", path], output, error).ShouldBe(2);
            File.WriteAllText(path, "invalid tcg");
            IdentityCommand.Run(["--old", path, "--new", path], output, error).ShouldBe(2);
        }
        finally { File.Delete(path); }
    }
}
