using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Фильтр пар элементов по матрице разделов, исключениям наборов и соединениям MEP.</summary>
internal sealed class PairFilter
{
    private readonly ClashScene _scene;
    private readonly bool _ignoreConnected;
    private readonly HashSet<(Guid, Guid)> _exclusions = [];
    private readonly bool[] _enabled;
    private readonly float[] _tolerance;
    private readonly int _disciplineCount;

    public PairFilter(ClashScene scene, ClashConfiguration config)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        if (config is null) throw new ArgumentNullException(nameof(config));
        _ignoreConnected = config.IgnoreConnectedMep;

        foreach (var e in config.Exclusions) _exclusions.Add(Normalize(e.SetA, e.SetB));

        _disciplineCount = scene.Disciplines.Count;
        _enabled = new bool[_disciplineCount * _disciplineCount];
        _tolerance = new float[_disciplineCount * _disciplineCount];
        for (var i = 0; i < _disciplineCount; i++)
        {
            for (var j = 0; j < _disciplineCount; j++)
            {
                var cell = config.CellFor(scene.Disciplines[i], scene.Disciplines[j]);
                _enabled[i * _disciplineCount + j] = cell is { Enabled: true };
                _tolerance[i * _disciplineCount + j] = (float)(cell?.ToleranceMm ?? 0);
            }
        }
    }

    /// <summary>Включена ли ячейка матрицы для пары разделов.</summary>
    public bool IsCellEnabled(int disciplineA, int disciplineB) => _enabled[disciplineA * _disciplineCount + disciplineB];

    /// <summary>Допуск ячейки матрицы для пары разделов, мм.</summary>
    public float ToleranceOf(int disciplineA, int disciplineB) => _tolerance[disciplineA * _disciplineCount + disciplineB];

    /// <summary>Проходит ли пара элементов фильтр; <paramref name="toleranceMm"/> - допуск ячейки матрицы.</summary>
    public bool Passes(int a, int b, out float toleranceMm)
    {
        toleranceMm = 0;
        if (a == b) return false;

        var ea = _scene.Elements[a];
        var eb = _scene.Elements[b];
        if (!IsCellEnabled(ea.Discipline, eb.Discipline)) return false;
        if (_ignoreConnected && ea.ModelIndex == eb.ModelIndex && _scene.AreConnected(a, b)) return false;
        if (IsExcluded(ea.Sets, eb.Sets)) return false;

        toleranceMm = ToleranceOf(ea.Discipline, eb.Discipline);
        return true;
    }

    // Пара исключается, только если исключено каждое сочетание наборов.
    private bool IsExcluded(Guid[] setsA, Guid[] setsB)
    {
        if (_exclusions.Count == 0) return false;
        foreach (var x in setsA)
        {
            foreach (var y in setsB)
            {
                if (!_exclusions.Contains(Normalize(x, y))) return false;
            }
        }

        return true;
    }

    private static (Guid, Guid) Normalize(Guid x, Guid y) => x.CompareTo(y) <= 0 ? (x, y) : (y, x);
}
