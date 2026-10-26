using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>BVH по габаритам элементов: разбиение по медиане центров вдоль самой длинной оси, листья не более 4 элементов.</summary>
internal sealed class Bvh
{
    private const int LeafSize = 4;

    private readonly Node[] _nodes;
    private int _nodeCount;

    public Bvh(IReadOnlyList<SceneElement> elements, int[] indices)
    {
        Items = (int[])indices.Clone();
        ItemBounds = new Aabb[Items.Length];
        for (var i = 0; i < Items.Length; i++) ItemBounds[i] = elements[Items[i]].Bounds;

        _nodes = new Node[Math.Max(1, 2 * Items.Length)];
        if (Items.Length > 0) Build(0, Items.Length);
    }

    /// <summary>Индексы элементов сцены в порядке листьев.</summary>
    public int[] Items { get; }

    /// <summary>Габариты элементов в порядке <see cref="Items"/>.</summary>
    public Aabb[] ItemBounds { get; }

    public bool IsEmpty => Items.Length == 0;

    public ref readonly Node this[int node] => ref _nodes[node];

    private int Build(int start, int count)
    {
        var index = _nodeCount++;
        var bounds = Aabb.Empty;
        var centers = Aabb.Empty;
        for (var i = start; i < start + count; i++)
        {
            bounds = bounds.Union(ItemBounds[i]);
            var c = ItemBounds[i].Center;
            centers = new Aabb(Vector3.Min(centers.Min, c), Vector3.Max(centers.Max, c));
        }

        if (count <= LeafSize)
        {
            _nodes[index] = new Node(bounds, -1, -1, start, count);
            return index;
        }

        var extent = centers.Max - centers.Min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var keys = new float[count];
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = Axis(ItemBounds[start + i].Center, axis);
            order[i] = i;
        }

        Array.Sort(keys, order);
        var items = new int[count];
        var itemBounds = new Aabb[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = Items[start + order[i]];
            itemBounds[i] = ItemBounds[start + order[i]];
        }

        Array.Copy(items, 0, Items, start, count);
        Array.Copy(itemBounds, 0, ItemBounds, start, count);

        var half = count / 2;
        var left = Build(start, half);
        var right = Build(start + half, count - half);
        _nodes[index] = new Node(bounds, left, right, start, 0);
        return index;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Узел дерева; лист, если <see cref="Count"/> больше нуля.</summary>
    public readonly struct Node(Aabb bounds, int left, int right, int start, int count)
    {
        public Aabb Bounds { get; } = bounds;
        public int Left { get; } = left;
        public int Right { get; } = right;
        public int Start { get; } = start;
        public int Count { get; } = count;
        public bool IsLeaf => Count > 0;
    }
}
