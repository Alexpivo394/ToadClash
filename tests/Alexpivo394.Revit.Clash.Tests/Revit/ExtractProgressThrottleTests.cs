using Shouldly;
using Alexpivo394.Revit.Clash.Revit;

namespace Alexpivo394.Revit.Clash.Tests.Revit;

public sealed class ExtractProgressThrottleTests
{
    private static List<int> Reported(int total, TimeSpan step)
    {
        var throttle = new ProgressThrottle(500, TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.UnixEpoch;
        var reported = new List<int>();
        for (var done = 1; done <= total; done++)
        {
            if (throttle.ShouldReport(done, total, start + TimeSpan.FromTicks(step.Ticks * (done - 1)))) reported.Add(done);
        }

        return reported;
    }

    [Test]
    public void ShouldReport_FirstAndLastAlways() => Reported(2000, TimeSpan.FromMilliseconds(1)).ShouldBe([1, 2000]);

    [Test]
    public void ShouldReport_EveryNButNotFasterThanInterval() => Reported(2000, TimeSpan.FromSeconds(1)).ShouldBe([1, 500, 1000, 1500, 2000]);

    [Test]
    public void ShouldReport_SingleElement_ReportedOnce() => Reported(1, TimeSpan.FromSeconds(1)).ShouldBe([1]);
}
