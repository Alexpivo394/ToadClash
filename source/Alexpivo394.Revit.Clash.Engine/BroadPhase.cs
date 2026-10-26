using System.Collections.Concurrent;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Кандидат на пересечение: индексы элементов сцены, A меньше B, и допуск ячейки матрицы.</summary>
internal readonly record struct CandidatePair(int A, int B, float ToleranceMm);

/// <summary>Широкая фаза: пары элементов с пересекающимися габаритами, прошедшие <see cref="PairFilter"/>.</summary>
internal static class BroadPhase
{
    private const int WorkItemsPerThread = 16;

    /// <summary>Находит кандидатов; каждая пара возвращается один раз, порядок не определён.</summary>
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

        var disciplineCount = scene.Disciplines.Count;
        var groups = new List<int>[disciplineCount];
        for (var d = 0; d < disciplineCount; d++) groups[d] = [];
        for (var i = 0; i < scene.Elements.Count; i++) groups[scene.Elements[i].Discipline].Add(i);

        var trees = new Bvh[disciplineCount];
        for (var d = 0; d < disciplineCount; d++) trees[d] = new Bvh(scene.Elements, [.. groups[d]]);

        var items = new List<WorkItem>();
        for (var i = 0; i < disciplineCount; i++)
        {
            for (var j = i; j < disciplineCount; j++)
            {
                if (trees[i].IsEmpty || trees[j].IsEmpty || !filter.IsCellEnabled(i, j)) continue;
                items.Add(new WorkItem(trees[i], trees[j], i == j, 0, 0));
            }
        }

        var work = Expand(items, threads * WorkItemsPerThread);
        var results = new ConcurrentBag<List<CandidatePair>>();
        Parallel.ForEach(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            () => new List<CandidatePair>(),
            (item, _, local) =>
            {
                var walker = new Walker(filter, local);
                if (item.Self) walker.Self(item.A, item.NodeA);
                else walker.Cross(item.A, item.NodeA, item.B, item.NodeB);
                return local;
            },
            local => results.Add(local));

        var all = new List<CandidatePair>(results.Sum(r => r.Count));
        foreach (var part in results) all.AddRange(part);
        return all;
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
                    AddIfOverlap(next, new WorkItem(t.A, t.A, false, a.Left, a.Right));
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

    private readonly record struct WorkItem(Bvh A, Bvh B, bool Self, int NodeA, int NodeB);

    private sealed class Walker(PairFilter filter, List<CandidatePair> output)
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

        private void Emit(int x, int y)
        {
            var (a, b) = x < y ? (x, y) : (y, x);
            if (filter.Passes(a, b, out var tolerance)) output.Add(new CandidatePair(a, b, tolerance));
        }
    }
}
