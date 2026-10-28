namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>
///     Решает, сообщать ли о ходе: о первом и последнем шаге всегда, о промежуточных — каждые <c>every</c> шагов,
///     но не чаще <c>interval</c>. Без Revit API — подключается в тесты ссылкой на файл.
/// </summary>
internal sealed class ProgressThrottle(int every, TimeSpan interval)
{
    private DateTimeOffset? _last;

    public bool ShouldReport(int done, int total, DateTimeOffset now)
    {
        var report = done <= 1 || done >= total || (done % every == 0 && (_last is not { } last || now - last >= interval));
        if (report) _last = now;
        return report;
    }
}
