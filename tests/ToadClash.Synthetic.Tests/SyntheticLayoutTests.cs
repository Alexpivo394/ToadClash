using Shouldly;
using ToadClash.DevAddin;

namespace ToadClash.Synthetic.Tests;

public class SyntheticLayoutTests
{
    [Test]
    public void DefaultSet_HasAtLeast150000PrimaryElements()
    {
        var options = new SyntheticOptions();
        options.Sections.Count.ShouldBe(5);
        options.MinimumElementCount.ShouldBeGreaterThanOrEqualTo(150000);
    }

    [Test]
    public void Layout_IsDeterministicDistinctAndUsesMillimetres()
    {
        var options = new SyntheticOptions("ВК", 2, 26);
        var cells = SyntheticLayout.Cells(options).ToArray();
        cells.Length.ShouldBe(52);
        cells.Distinct().Count().ShouldBe(52);
        cells[0].ShouldBe(new SyntheticCell(0, 0, 0, 0));
        cells[25].ShouldBe(new SyntheticCell(0, 0, 8000, 0));
        cells[26].ShouldBe(new SyntheticCell(1, 0, 0, 4000));
        SyntheticLayout.Cells(options).ToArray().ShouldBe(cells);
        options.MinimumElementCount.ShouldBe(156);
    }

    [Test]
    public void InvalidOptions_AreRejectedBeforeCreatingDocuments()
    {
        Should.Throw<ArgumentException>(() => new SyntheticOptions("../../work"));
        Should.Throw<ArgumentOutOfRangeException>(() => new SyntheticOptions(floors: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new SyntheticOptions(floors: 101));
        Should.Throw<ArgumentOutOfRangeException>(() => new SyntheticOptions(cellsPerFloor: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new SyntheticOptions(cellsPerFloor: 501));
    }
}
