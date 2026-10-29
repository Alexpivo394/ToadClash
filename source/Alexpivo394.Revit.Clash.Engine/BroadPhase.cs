using System.Collections.Concurrent;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Клетка матрицы, в которой встретилась пара: индексы групп сцены (меньший первым) и допуск клетки.</summary>
internal readonly record struct PairCell(int GroupA, int GroupB, float ToleranceMm);

/// <summary>
///     Кандидат на пересечение: индексы элементов сцены, A меньше B, все клетки, в которых встретилась пара, и наименьший их
///     допуск — с ним идёт точная фаза.
/// </summary>
internal readonly record struct CandidatePair(int A, int B, float ToleranceMm, PairCell[] Cells);

/// <summary>Широкая фаза: пары элементов с пересекающимися габаритами, прошедшие <see cref="PairFilter"/>.</summary>
internal static class BroadPhase
{
    private const int WorkItemsPerThread = 16;

    /// <summary>
    ///     Находит кандидатов. Дерево строится на каждую группу, элемент нескольких групп лежит в нескольких деревьях; пара,
    ///     встреченная в нескольких клетках, возвращается один раз со всеми клетками. Порядок — по (A, B).
    /// </summary>
    /// <param name="scene">Сцена.</param>
    /// <param name="config">Конфигурация проверки.</param>
    /// <param name="filter">Фильтр пар.</param>
    /// <param name="threads">Число потоков; не больше нуля означает число процессоров.</param>
    public static List<CandidatePair> Find(ClashScene scene, ClashConfiguration config, PairFilter filter, int threads)
    {
        if (scene is null) throw new ArgumentNullException(nameof(scene));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (filter is null) throw new ArgumentNullException(nameof(filter));
        if (threads <= 0) threads = Environment.ProcessorCount;

        var groupCount = scene.Groups.Count;
        var members = new List<int>[groupCount];
        for (var g = 0; g < groupCount; g++) members[g] = [];
        for (var i = 0; i < scene.Elements.Count; i++)
        {
            foreach (var g in scene.Elements[i].Groups) members[g].Add(i);
        }

        var trees = new Bvh[groupCount];
        for (var g = 0; g < groupCount; g++) trees[g] = new Bvh(scene.Elements, [.. members[g]]);

        var items = new List<WorkItem>();
        for (var i = 0; i < groupCount; i++)
        {
            for (var j = i; j < groupCount; j++)
            {
                if (trees[i].IsEmpty || trees[j].IsEmpty || !filter.IsCellEnabled(i, j)) continue;
                items.Add(new WorkItem(trees[i], trees[j], i == j, 0, 0, i, j));
            }
        }

        var work = Expand(items, threads * WorkItemsPerThread);
        var results = new ConcurrentBag<List<RawPair>>();
        Parallel.ForEach(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            () => new List<RawPair>(),
            (item, _, local) =>
            {
                var walker = new Walker(filter, local, item.GroupA, item.GroupB);
                if (item.Self) walker.Self(item.A, item.NodeA);
                else walker.Cross(item.A, item.NodeA, item.B, item.NodeB);
                return local;
            },
            local => results.Add(local));

        var raw = new List<RawPair>(results.Sum(r => r.Count));
        foreach (var part in results) raw.AddRange(part);
        return Merge(raw);
    }

    // Одна пара из разных клеток (или дважды из одной клетки, когда оба элемента в обеих её группах) — один кандидат.
    private static List<CandidatePair> Merge(List<RawPair> raw)
    {
        raw.Sort(static (x, y) =>
        {
            var byA = x.A.CompareTo(y.A);
            if (byA != 0) return byA;
            var byB = x.B.CompareTo(y.B);
            if (byB != 0) return byB;
            var byGroupA = x.Cell.GroupA.CompareTo(y.Cell.GroupA);
            return byGroupA != 0 ? byGroupA : x.Cell.GroupB.CompareTo(y.Cell.GroupB);
        });

        var result = new List<CandidatePair>(raw.Count);
        var cells = new List<PairCell>(4);
        for (var i = 0; i < raw.Count;)
        {
            var (a, b) = (raw[i].A, raw[i].B);
            cells.Clear();
            var tolerance = float.MaxValue;
            for (; i < raw.Count && raw[i].A == a && raw[i].B == b; i++)
            {
                var cell = raw[i].Cell;
                if (cells.Count > 0 && cells[cells.Count - 1].GroupA == cell.GroupA && cells[cells.Count - 1].GroupB == cell.GroupB) continue;
                cells.Add(cell);
                tolerance = Math.Min(tolerance, cell.ToleranceMm);
            }

            result.Add(new CandidatePair(a, b, tolerance, [.. cells]));
        }

        return result;
    }

    // Делит пары поддеревьев, пока их не станет достаточно для равномерной загрузки потоков.
    private static List<WorkItem> Expand(List<WorkItem> start, int wanted)
    {
        var current = start;
        while (current.Count < wanted)
        {
            var next = new List<WorkItem>(current.Count * 2);
            var grew = false;
            foreach (var t in current)
            {
                var a = t.A[t.NodeA];
                if (t.Self)
                {
                    if (a.IsLeaf)
                    {
                        next.Add(t);
                        continue;
                    }

                    next.Add(t with { NodeA = a.Left, NodeB = a.Left });
                    next.Add(t with { NodeA = a.Right, NodeB = a.Right });
                    AddIfOverlap(next, new WorkItem(t.A, t.A, false, a.Left, a.Right, t.GroupA, t.GroupB));
                    grew = true;
                    continue;
                }

                var b = t.B[t.NodeB];
                if (a.IsLeaf && b.IsLeaf)
                {
                    next.Add(t);
                    continue;
                }

                grew = true;
                if (!a.IsLeaf && (b.IsLeaf || Volume(a) >= Volume(b)))
                {
                    AddIfOverlap(next, t with { NodeA = a.Left });
                    AddIfOverlap(next, t with { NodeA = a.Right });
                }
                else
                {
                    AddIfOverlap(next, t with { NodeB = b.Left });
                    AddIfOverlap(next, t with { NodeB = b.Right });
                }
            }

            current = next;
            if (!grew) break;
        }

        return current;
    }

    private static void AddIfOverlap(List<WorkItem> list, WorkItem t)
    {
        if (t.A[t.NodeA].Bounds.Overlaps(t.B[t.NodeB].Bounds)) list.Add(t);
    }

    private static float Volume(in Bvh.Node node)
    {
        var e = node.Bounds.Max - node.Bounds.Min;
        return e.X * e.Y * e.Z;
    }

    private readonly record struct WorkItem(Bvh A, Bvh B, bool Self, int NodeA, int NodeB, int GroupA, int GroupB);

    /// <summary>Пара из одной клетки: элемент A входит в группу клетки GroupA, B — в GroupB.</summary>
    private readonly record struct RawPair(int A, int B, PairCell Cell);

    private sealed class Walker(PairFilter filter, List<RawPair> output, int groupA, int groupB)
    {
        public void Self(Bvh tree, int nodeIndex)
        {
            ref readonly var node = ref tree[nodeIndex];
            if (node.IsLeaf)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    for (var j = i + 1; j < node.Start + node.Count; j++)
                    {
                        if (tree.ItemBounds[i].Overlaps(tree.ItemBounds[j])) Emit(tree.Items[i], tree.Items[j]);
                    }
                }

                return;
            }

            Self(tree, node.Left);
            Self(tree, node.Right);
            Cross(tree, node.Left, tree, node.Right);
        }

        public void Cross(Bvh ta, int na, Bvh tb, int nb)
        {
            ref readonly var a = ref ta[na];
            ref readonly var b = ref tb[nb];
            if (!a.Bounds.Overlaps(b.Bounds)) return;

            if (a.IsLeaf && b.IsLeaf)
            {
                for (var i = a.Start; i < a.Start + a.Count; i++)
                {
                    for (var j = b.Start; j < b.Start + b.Count; j++)
                    {
                        if (ta.ItemBounds[i].Overlaps(tb.ItemBounds[j])) Emit(ta.Items[i], tb.Items[j]);
                    }
                }

                return;
            }

            if (!a.IsLeaf && (b.IsLeaf || Volume(a) >= Volume(b)))
            {
                Cross(ta, a.Left, tb, nb);
                Cross(ta, a.Right, tb, nb);
            }
            else
            {
                Cross(ta, na, tb, b.Left);
                Cross(ta, na, tb, b.Right);
            }
        }

        // x — из дерева groupA, y — из дерева groupB.
        private void Emit(int x, int y)
        {
            if (!filter.Passes(x, y, groupA, groupB, out var tolerance)) return;
            var (a, b) = x < y ? (x, y) : (y, x);
            var cell = groupA <= groupB ? new PairCell(groupA, groupB, tolerance) : new PairCell(groupB, groupA, tolerance);
            output.Add(new RawPair(a, b, cell));
        }
    }
}
