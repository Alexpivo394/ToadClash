using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Прошлый прогон той же конфигурации: состояние элементов и найденные коллизии. Пара, у обоих элементов которой состояние не
///     изменилось, в точную фазу не идёт: коллизия берётся из прошлого прогона, а если её не было — её нет и сейчас.
/// </summary>
/// <remarks>
///     Состояние элемента — отпечаток геометрии, наборы, соединения MEP и корень изделия: всё, от чего зависит, проверялась ли
///     пара и чем кончилась проверка. Конфигурация должна совпадать с прошлой — это проверяет вызывающий.
/// </remarks>
public sealed class IncrementalBase
{
    private readonly Dictionary<ElementRef, ulong> _states;
    private readonly Dictionary<(ElementRef, ElementRef), ClashHit> _hits;

    private IncrementalBase(Dictionary<ElementRef, ulong> states, Dictionary<(ElementRef, ElementRef), ClashHit> hits)
    {
        _states = states;
        _hits = hits;
    }

    /// <summary>Основа из описаний элементов прошлых выгрузок, их отпечатков и коллизий прошлого прогона.</summary>
    /// <param name="previousModels">Элементы и параметры прошлых выгрузок, без сеток.</param>
    /// <param name="previousFingerprints">Отпечатки геометрии элементов прошлых выгрузок.</param>
    /// <param name="config">Конфигурация прошлого прогона — она же нынешняя.</param>
    /// <param name="previousHits">Все коллизии прошлого прогона.</param>
    public static IncrementalBase From(IReadOnlyList<ElementsInput> previousModels, IReadOnlyDictionary<ElementRef, ulong> previousFingerprints,
        ClashConfiguration config, IReadOnlyList<ClashHit> previousHits)
    {
        if (previousModels is null) throw new ArgumentNullException(nameof(previousModels));
        if (previousFingerprints is null) throw new ArgumentNullException(nameof(previousFingerprints));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (previousHits is null) throw new ArgumentNullException(nameof(previousHits));

        var membership = SetMembership.Evaluate(config, previousModels);
        var states = new Dictionary<ElementRef, ulong>();
        foreach (var model in previousModels)
        foreach (var info in model.Elements)
        {
            var element = new ElementRef(model.ModelKey, info.UniqueId);
            if (!previousFingerprints.TryGetValue(element, out var fingerprint)) continue;
            states[element] = StateOf(fingerprint, membership.SetsOf(model.ModelKey, info.UniqueId), info);
        }

        var hits = new Dictionary<(ElementRef, ElementRef), ClashHit>(previousHits.Count);
        foreach (var hit in previousHits) hits[(hit.A, hit.B)] = hit;
        return new IncrementalBase(states, hits);
    }

    /// <summary>Состояние элемента в прошлом прогоне; false — элемента не было.</summary>
    internal bool TryGetState(ElementRef element, out ulong state) => _states.TryGetValue(element, out state);

    /// <summary>Коллизия пары в прошлом прогоне (элементы в каноническом порядке); null — не было.</summary>
    internal ClashHit? HitOf(ElementRef a, ElementRef b) => _hits.TryGetValue((a, b), out var hit) ? hit : null;

    /// <summary>Хеш всего, от чего зависит проверка пары с участием элемента при той же конфигурации.</summary>
    internal static ulong StateOf(ulong fingerprint, IReadOnlyList<Guid> sets, ElementInfo info)
    {
        var hash = new System.IO.Hashing.XxHash64();
        Span<byte> buffer = stackalloc byte[16];
        BitConverter.TryWriteBytes(buffer, fingerprint);
        hash.Append(buffer[..8]);
        foreach (var set in sets.OrderBy(s => s))
        {
            set.TryWriteBytes(buffer);
            hash.Append(buffer);
        }

        Append(hash, "|root:" + info.RootComponentUniqueId);
        foreach (var connected in info.ConnectedUniqueIds.OrderBy(c => c, StringComparer.Ordinal)) Append(hash, "|c:" + connected);
        return hash.GetCurrentHashAsUInt64();
    }

    private static void Append(System.IO.Hashing.XxHash64 hash, string text) => hash.Append(System.Text.Encoding.UTF8.GetBytes(text));
}
