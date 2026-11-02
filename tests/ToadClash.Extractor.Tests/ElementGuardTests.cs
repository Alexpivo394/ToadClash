using Alexpivo394.Revit.Clash.Revit;
using Shouldly;

namespace ToadClash.Extractor.Tests;

public sealed class ElementGuardTests
{
    [Test]
    public void TryRun_RevitException_RecordsAndReturnsFalse()
    {
        var skipped = new List<SkippedElement>();

        ElementGuard.TryRun("u1", 1, "геометрия", () => throw new Autodesk.Revit.Exceptions.ApplicationException(), skipped).ShouldBeFalse();
        ElementGuard.TryRun("u2", 2, "параметры", () => throw new InvalidOperationException("сбой"), skipped).ShouldBeFalse();
        ElementGuard.TryRun("u3", 3, "геометрия", () => { }, skipped).ShouldBeTrue();

        skipped.Select(s => (s.UniqueId, s.ElementId, s.Stage)).ShouldBe([("u1", 1L, "геометрия"), ("u2", 2L, "параметры")]);
        skipped[1].Message.ShouldBe("сбой");
    }

    [Test]
    public void TryRun_Cancellation_Propagates()
    {
        var skipped = new List<SkippedElement>();

        Should.Throw<OperationCanceledException>(() => ElementGuard.TryRun("u", 1, "геометрия", () => throw new OperationCanceledException(), skipped));
        Should.Throw<NullReferenceException>(() => ElementGuard.TryRun("u", 1, "геометрия", () => throw new NullReferenceException(), skipped));
        skipped.ShouldBeEmpty();
    }

    [Test]
    public void ThrowIfTooMany_Boundaries()
    {
        Should.NotThrow(() => ElementGuard.ThrowIfTooMany(Skipped(20), 100));
        Should.NotThrow(() => ElementGuard.ThrowIfTooMany(Skipped(21), 10000));
        var error = Should.Throw<InvalidOperationException>(() => ElementGuard.ThrowIfTooMany(Skipped(21), 100));
        error.Message.ShouldStartWith("Пропущено 21 элементов из 100: модель повреждена. Первые: u0, u1,");
        error.Message.ShouldNotContain("u10");
    }

    [Test]
    public void ThrowIfTooMany_CountsElementsNotStages()
    {
        // Сбой геометрии и параметров одного элемента — один пропущенный элемент.
        var both = Skipped(21).SelectMany(s => new[] { s, s with { Stage = "параметры" } }).Take(40).ToList();

        Should.NotThrow(() => ElementGuard.ThrowIfTooMany(both, 100));
    }

    private static List<SkippedElement> Skipped(int count) =>
        Enumerable.Range(0, count).Select(i => new SkippedElement($"u{i}", i, "геометрия", "сбой")).ToList();
}
