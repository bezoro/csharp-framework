namespace Bezoro.Navigation;

/// <summary>
/// An immutable row-major grid. Zero blocks a cell; positive costs weight entering it.
/// The constructor copies caller data, allowing the resulting map to be shared by independent searches.
/// </summary>
public sealed class NavigationGrid
{
    private readonly ushort[] _costs;

    /// <summary>Copies exactly <paramref name="width"/> times <paramref name="height"/> row-major costs.</summary>
    public NavigationGrid(int width, int height, ReadOnlySpan<ushort> costs)
    {
        int cellCount = ValidateDimensions(width, height);
        if (costs.Length != cellCount)
            throw new ArgumentException("Cost count must equal the grid area.", nameof(costs));
        Width = width;
        Height = height;
        _costs = costs.ToArray();
        MinimumTraversalCost = FindMinimumCost(_costs);
    }

    /// <summary>Creates a uniform grid. A zero cost creates a completely blocked grid.</summary>
    public NavigationGrid(int width, int height, ushort traversalCost = 1)
    {
        int cellCount = ValidateDimensions(width, height);
        Width = width;
        Height = height;
        _costs = new ushort[cellCount];
        if (traversalCost != 0) Array.Fill(_costs, traversalCost);
        MinimumTraversalCost = traversalCost;
    }

    /// <summary>Gets the number of columns.</summary>
    public int Width { get; }

    /// <summary>Gets the number of rows.</summary>
    public int Height { get; }

    /// <summary>Gets the number of cells.</summary>
    public int CellCount => _costs.Length;

    /// <summary>Gets the smallest positive cost, or zero when every cell is blocked.</summary>
    public ushort MinimumTraversalCost { get; }

    /// <summary>Gets a read-only view of row-major traversal costs.</summary>
    public ReadOnlySpan<ushort> Costs => _costs;

    /// <summary>Gets the traversal cost at a valid coordinate.</summary>
    public ushort this[int x, int y] => _costs[GetIndex(new GridPoint(x, y))];

    /// <summary>Returns whether the coordinate lies inside the grid.</summary>
    public bool Contains(GridPoint point) => unchecked((uint)point.X) < (uint)Width && unchecked((uint)point.Y) < (uint)Height;

    /// <summary>Returns whether a coordinate is inside the grid and has a positive cost.</summary>
    public bool IsWalkable(GridPoint point) => Contains(point) && _costs[point.Y * Width + point.X] != 0;

    internal int GetIndex(GridPoint point)
    {
        if (!Contains(point)) throw new ArgumentOutOfRangeException(nameof(point), "Coordinate is outside the grid.");
        return point.Y * Width + point.X;
    }

    internal ushort GetCost(int index) => _costs[index];

    private static int ValidateDimensions(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        if ((long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(height), "Grid area must fit in an Int32.");
        return width * height;
    }

    private static ushort FindMinimumCost(ReadOnlySpan<ushort> costs)
    {
        ushort minimum = ushort.MaxValue;
        bool anyWalkable = false;
        foreach (ushort cost in costs)
        {
            if (cost == 0) continue;
            anyWalkable = true;
            if (cost < minimum) minimum = cost;
        }
        return anyWalkable ? minimum : (ushort)0;
    }
}
