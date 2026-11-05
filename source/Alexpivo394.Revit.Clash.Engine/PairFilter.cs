using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Фильтр пар элементов по матрице групп, исключениям наборов, соединениям MEP и вложенным семействам одного изделия.</summary>
internal sealed class PairFilter
{
    private readonly ClashScene _scene;
    private readonly bool _ignoreConnected;
    private readonly HashSet<(Guid, Guid)> _exclusions = [];
    private readonly bool[] _enabled;
    private readonly float[] _tolerance;
    private readonly int _groupCount;

    public PairFilter(ClashScene scene, ClashConfiguration config)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        if (config is null) throw new ArgumentNullException(nameof(config));
        _ignoreConnected = config.IgnoreConnectedMep;

        foreach (var e in config.ExcludedSetPairs()) _exclusions.Add(Normalize(e.SetA, e.SetB));

        _groupCount = scene.Groups.Count;
        _enabled = new bool[_groupCount * _groupCount];
        _tolerance = new float[_groupCount * _groupCount];
        for (var i = 0; i < _groupCount; i++)
        {
            for (var j = 0; j < _groupCount; j++)
            {
                var cell = config.CellFor(scene.Groups[i], scene.Groups[j]);
                _enabled[i * _groupCount + j] = cell is { Enabled: true };
                _tolerance[i * _groupCount + j] = (float)(cell?.ToleranceMm ?? 0);
            }
        }
    }

    /// <summary>Включена ли клетка матрицы для пары групп сцены.</summary>
    public bool IsCellEnabled(int groupA, int groupB) => _enabled[groupA * _groupCount + groupB];

    /// <summary>Допуск клетки матрицы для пары групп сцены, мм.</summary>
    public float ToleranceOf(int groupA, int groupB) => _tolerance[groupA * _groupCount + groupB];

    /// <summary>
    ///     Проходит ли пара элементов фильтр в клетке групп <paramref name="groupA"/> × <paramref name="groupB"/>; элемент
    ///     <paramref name="a"/> должен входить в первую группу, <paramref name="b"/> — во вторую.
    /// </summary>
    public bool Passes(int a, int b, int groupA, int groupB, out float toleranceMm)
    {
        toleranceMm = 0;
        if (a == b || !IsCellEnabled(groupA, groupB)) return false;

        var ea = _scene.Elements[a];
        var eb = _scene.Elements[b];
        if (ea.ModelIndex == eb.ModelIndex && ea.Product == eb.Product) return false;
        if (_ignoreConnected && ea.ModelIndex == eb.ModelIndex && _scene.AreConnected(a, b)) return false;
        if (IsExcluded(ea.Sets, eb.Sets)) return false;

        toleranceMm = ToleranceOf(groupA, groupB);
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
