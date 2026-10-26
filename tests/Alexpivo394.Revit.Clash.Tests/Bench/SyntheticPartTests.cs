using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;
using ToadClash.Bench;

namespace Alexpivo394.Revit.Clash.Tests.Bench;

public sealed class SyntheticPartTests
{
    private static List<(string Key, string Discipline, string Id, ulong Fingerprint, string Connected)> Summary(int seed) =>
        SyntheticPart.Generate(1200, 12, seed)
            .SelectMany(model => model.Geometry.Elements.Select(e => (
                model.Geometry.Header.ModelKey,
                model.Discipline,
                e.Info.UniqueId,
                e.Fingerprint,
                string.Join(",", e.Info.ConnectedUniqueIds))))
            .ToList();

    [Test]
    public void SyntheticPart_IsDeterministic()
    {
        Summary(1).ShouldBe(Summary(1));
    }

    [Test]
    public void SyntheticPart_DifferentSeed_DifferentGeometry()
    {
        Summary(1).Select(s => s.Fingerprint).ShouldNotBe(Summary(2).Select(s => s.Fingerprint));
    }

    [Test]
    public void SyntheticPart_HasRequestedElementsAndRealisticShape()
    {
        var part = SyntheticPart.Generate(1200, 12, 1);

        part.Count.ShouldBe(12);
        part.Sum(m => m.Geometry.Elements.Count).ShouldBe(1200);
        part.Select(m => m.Discipline).Distinct().Count().ShouldBe(8);
        part.ShouldAllBe(m => m.Geometry.Header.OriginX >= 1_000_000 || m.Geometry.Header.OriginY >= 1_000_000);

        foreach (var (geometry, _, _) in part)
        {
            var ids = geometry.Elements.Select(e => e.Info.UniqueId).ToHashSet();
            ids.Count.ShouldBe(geometry.Elements.Count);
            foreach (var element in geometry.Elements)
            {
                element.Mesh.TriangleCount.ShouldBeGreaterThan(0);
                element.Info.ConnectedUniqueIds.ShouldAllBe(id => ids.Contains(id));
            }
        }
    }

    [Test]
    public void SyntheticPart_HasRealisticTrianglesAndConnectedMep()
    {
        foreach (var profile in new[] { SyntheticProfile.Standard, SyntheticProfile.Dense })
        {
            var part = SyntheticPart.Generate(12000, 12, 1, profile);
            var elements = part.SelectMany(m => m.Geometry.Elements.Select(e => (m.Discipline, Element: e))).ToList();

            var average = elements.Average(e => (double)e.Element.Mesh.TriangleCount);
            average.ShouldBeInRange(150, 300);

            var mep = elements.Where(e => e.Discipline is not ("КР" or "АР")).ToList();
            mep.Count(e => e.Element.Info.ConnectedUniqueIds.Count > 0).ShouldBeGreaterThanOrEqualTo(mep.Count / 2);
        }
    }
}
