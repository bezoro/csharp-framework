using System.Numerics;
using System.Threading;
using Bezoro.Core.Collections;

namespace Bezoro.Spatial;

/// <summary>A bounded uniform-grid index of points with application-owned payloads.</summary>
/// <typeparam name="T">The stored payload type.</typeparam>
/// <remarks>
/// Queries may run concurrently. Mutations acquire exclusive access, including across a query's sizing and copying passes.
/// Payload references and destination storage retain their caller-defined ownership and synchronization requirements.
/// Each entry occupies one cell. Queries own no scratch storage and never allocate managed memory after construction.
/// Handles belong to this index; removal and clearing invalidate them. Results have no stable ordering.
/// </remarks>
public sealed class PointSpatialIndex<T>
{
    private const int None = -1;
    private readonly SlotMap<Node> _entries;
    private readonly int[] _heads;
    private readonly object _gate = new();
    private int _readers;
    private int _waitingWriters;
    private bool _writer;

    /// <summary>Allocates fixed entry and cell storage.</summary>
    /// <param name="worldBounds">Finite inclusive world bounds with positive width and height.</param>
    /// <param name="cellSize">Finite positive cell side length. The final row and column may be shorter.</param>
    /// <param name="capacity">Maximum number of entries. Zero creates an index that accepts no points.</param>
    /// <param name="maximumCells">Explicit upper limit on the computed cell count, checked before allocation.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound, capacity, cell size, or cell budget is invalid.</exception>
    public PointSpatialIndex(Bounds2D worldBounds, float cellSize, int capacity, int maximumCells)
    {
        if (worldBounds.Width <= 0 || worldBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(worldBounds), "World bounds must have positive width and height.");
        if (float.IsNaN(cellSize) || float.IsInfinity(cellSize) || cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumCells <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCells));

        double columns = Math.Ceiling(worldBounds.Width / cellSize);
        double rows = Math.Ceiling(worldBounds.Height / cellSize);
        if (columns > int.MaxValue || rows > int.MaxValue || columns * rows > maximumCells)
            throw new ArgumentOutOfRangeException(nameof(maximumCells), "The world grid exceeds the configured cell budget.");

        WorldBounds = worldBounds;
        CellSize = cellSize;
        Columns = (int)columns;
        Rows = (int)rows;
        CellCount = checked(Columns * Rows);
        _entries = new SlotMap<Node>(capacity);
        // A zero-capacity index never needs to inspect a bucket.
        _heads = capacity == 0 ? Array.Empty<int>() : new int[CellCount];
        Array.Fill(_heads, None);
    }

    /// <summary>Gets the inclusive world bounds.</summary>
    public Bounds2D WorldBounds { get; }
    /// <summary>Gets the configured cell side length.</summary>
    public float CellSize { get; }
    /// <summary>Gets the number of grid columns.</summary>
    public int Columns { get; }
    /// <summary>Gets the number of grid rows.</summary>
    public int Rows { get; }
    /// <summary>Gets the logical number of grid cells.</summary>
    public int CellCount { get; }
    /// <summary>Gets the number of live points.</summary>
    public int Count
    {
        get
        {
            EnterRead();
            try { return _entries.Count; }
            finally { ExitRead(); }
        }
    }
    /// <summary>Gets the fixed entry capacity.</summary>
    public int Capacity => _entries.Capacity;
    /// <summary>Gets the reusable entry capacity. Exhausted handle generations retire their slots.</summary>
    public int AvailableCapacity
    {
        get
        {
            EnterRead();
            try { return _entries.AvailableCapacity; }
            finally { ExitRead(); }
        }
    }

    /// <summary>Adds an in-world point and returns its owner-scoped handle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The point is non-finite or outside the world.</exception>
    /// <exception cref="InvalidOperationException">No entry capacity remains.</exception>
    public SlotHandle Add(Vector2 position, T value)
    {
        Bounds2D.ValidatePoint(position, nameof(position));
        if (!WorldBounds.Contains(position)) throw new ArgumentOutOfRangeException(nameof(position), "The point is outside the world.");
        if (!TryAdd(position, value, out var handle)) throw new InvalidOperationException("The spatial index has no reusable entry capacity.");
        return handle;
    }

    /// <summary>Adds a finite in-world point if capacity remains. Failure leaves the index unchanged and returns a default handle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The point is non-finite.</exception>
    public bool TryAdd(Vector2 position, T value, out SlotHandle handle)
    {
        EnterWrite();
        try
        {
            Bounds2D.ValidatePoint(position, nameof(position));
            handle = default;
            if (!WorldBounds.Contains(position) || _entries.AvailableCapacity == 0) return false;
            int cell = GetCell(position);
            int index = _entries.Count;
            var node = new Node { Position = position, Value = value, Cell = cell, DenseIndex = index, Previous = None, Next = _heads[cell] };
            if (!_entries.TryAdd(node, out handle)) return false;
            if (node.Next != None)
            {
                var next = _entries.Values[node.Next];
                next.Previous = index;
                SetNode(node.Next, next);
            }
            _heads[cell] = index;
            return true;
        }
        finally { ExitWrite(); }
    }

    /// <summary>Tests whether this index still owns a live entry for the handle.</summary>
    public bool Contains(SlotHandle handle)
    {
        EnterRead();
        try { return _entries.Contains(handle); }
        finally { ExitRead(); }
    }

    /// <summary>Copies a live entry, or returns false and the default hit for a stale, default, or foreign handle.</summary>
    public bool TryGet(SlotHandle handle, out SpatialHit<T> hit)
    {
        EnterRead();
        try
        {
            if (!_entries.TryGet(handle, out var node))
            {
                hit = default;
                return false;
            }
            hit = new SpatialHit<T>(handle, node.Position, node.Value);
            return true;
        }
        finally { ExitRead(); }
    }

    /// <summary>Moves a live entry without changing its identity. Outside-world or invalid handles return false without mutation.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The point is non-finite.</exception>
    public bool TryMove(SlotHandle handle, Vector2 position)
    {
        EnterWrite();
        try
        {
            Bounds2D.ValidatePoint(position, nameof(position));
            if (!WorldBounds.Contains(position) || !_entries.TryGet(handle, out var node)) return false;
            int cell = GetCell(position);
            if (cell != node.Cell)
            {
                Unlink(node);
                node.Cell = cell;
                node.Previous = None;
                node.Next = _heads[cell];
                if (node.Next != None)
                {
                    var next = _entries.Values[node.Next];
                    next.Previous = node.DenseIndex;
                    SetNode(node.Next, next);
                }
                _heads[cell] = node.DenseIndex;
            }
            node.Position = position;
            _entries[handle] = node;
            return true;
        }
        finally { ExitWrite(); }
    }

    /// <summary>Replaces a live payload without changing its position or handle. Invalid handles return false.</summary>
    public bool TrySetValue(SlotHandle handle, T value)
    {
        EnterWrite();
        try
        {
            if (!_entries.TryGet(handle, out var node)) return false;
            node.Value = value;
            _entries[handle] = node;
            return true;
        }
        finally { ExitWrite(); }
    }

    /// <summary>Removes a live point and releases its payload reference. Invalid handles return false.</summary>
    public bool Remove(SlotHandle handle)
    {
        EnterWrite();
        try
        {
            if (!_entries.TryGet(handle, out var node)) return false;
            Unlink(node);
            int index = node.DenseIndex;
            int last = _entries.Count - 1;
            _entries.Remove(handle);
            if (index != last)
            {
                // SlotMap moved its last node into this dense position. Its bucket links still name the old position.
                var moved = _entries.Values[index];
                moved.DenseIndex = index;
                if (moved.Previous == None) _heads[moved.Cell] = index;
                else
                {
                    var previous = _entries.Values[moved.Previous];
                    previous.Next = index;
                    SetNode(moved.Previous, previous);
                }
                if (moved.Next != None)
                {
                    var next = _entries.Values[moved.Next];
                    next.Previous = index;
                    SetNode(moved.Next, next);
                }
                SetNode(index, moved);
            }
            return true;
        }
        finally { ExitWrite(); }
    }

    /// <summary>Removes all points, releases payload references, and invalidates their handles while retaining storage.</summary>
    public void Clear()
    {
        EnterWrite();
        try
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Array.Fill(_heads, None);
        }
        finally { ExitWrite(); }
    }

    /// <summary>Finds points inside a closed rectangle, including all boundary points.</summary>
    /// <remarks>A small destination is left unchanged. Hit order is unspecified. Cells outside the world are skipped.</remarks>
    public SpatialQueryResult QueryRectangle(Bounds2D bounds, Span<SpatialHit<T>> destination)
    {
        EnterRead();
        try
        {
            if (_entries.Count == 0 || !WorldBounds.Intersects(bounds)) return default;
            var query = new Query(bounds.Minimum.X, bounds.Minimum.Y, bounds.Maximum.X, bounds.Maximum.Y);
            return QueryCore(query, destination);
        }
        finally { ExitRead(); }
    }

    /// <summary>Finds points at most the supplied radius from a finite center, including the circle boundary.</summary>
    /// <remarks>
    /// Points must satisfy both the binary64 circle bounding rectangle and squared-distance comparison.
    /// A small destination is left unchanged; order is unspecified.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The center is non-finite or the radius is non-finite or negative.</exception>
    public SpatialQueryResult QueryCircle(Vector2 center, float radius, Span<SpatialHit<T>> destination)
    {
        EnterRead();
        try
        {
            Bounds2D.ValidatePoint(center, nameof(center));
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
            if (_entries.Count == 0) return default;
            var query = new Query((double)center.X - radius, (double)center.Y - radius,
                (double)center.X + radius, (double)center.Y + radius, center, (double)radius * radius);
            if (query.MaxX < WorldBounds.Minimum.X || query.MinX > WorldBounds.Maximum.X ||
                query.MaxY < WorldBounds.Minimum.Y || query.MinY > WorldBounds.Maximum.Y) return default;
            return QueryCore(query, destination);
        }
        finally { ExitRead(); }
    }

    private SpatialQueryResult QueryCore(in Query query, Span<SpatialHit<T>> destination)
    {
        int minColumn = GetColumn(query.MinX);
        int maxColumn = GetColumn(query.MaxX);
        int minRow = GetRow(query.MinY);
        int maxRow = GetRow(query.MaxY);
        // A destination covering every live point cannot overflow, so its common case needs one traversal.
        if (destination.Length >= _entries.Count)
        {
            int written = Visit(query, minColumn, maxColumn, minRow, maxRow, destination, publish: true);
            return new(SpatialQueryStatus.Success, written, written);
        }
        int required = Visit(query, minColumn, maxColumn, minRow, maxRow, default, publish: false);
        if (required > destination.Length) return new(SpatialQueryStatus.DestinationTooSmall, 0, required);
        if (required != 0) Visit(query, minColumn, maxColumn, minRow, maxRow, destination, publish: true);
        return new(SpatialQueryStatus.Success, required, required);
    }

    private int Visit(in Query query, int minColumn, int maxColumn, int minRow, int maxRow,
        Span<SpatialHit<T>> destination, bool publish)
    {
        int count = 0;
        var nodes = _entries.Values;
        for (int row = minRow; row <= maxRow; row++)
        for (int column = minColumn; column <= maxColumn; column++)
        {
            for (int index = _heads[row * Columns + column]; index != None; index = nodes[index].Next)
            {
                ref readonly var node = ref nodes[index];
                if (!query.Contains(node.Position)) continue;
                if (publish) destination[count] = new SpatialHit<T>(_entries.GetHandleAt(index), node.Position, node.Value);
                count++;
            }
        }
        return count;
    }

    private void Unlink(in Node node)
    {
        if (node.Previous == None) _heads[node.Cell] = node.Next;
        else
        {
            var previous = _entries.Values[node.Previous];
            previous.Next = node.Next;
            SetNode(node.Previous, previous);
        }
        if (node.Next != None)
        {
            var next = _entries.Values[node.Next];
            next.Previous = node.Previous;
            SetNode(node.Next, next);
        }
    }

    private void SetNode(int index, Node node) => _entries[_entries.GetHandleAt(index)] = node;

    // The index has no callbacks or public borrowed views. Operations take this gate once;
    // private algorithms run directly over storage. Waiting writers prevent writer starvation.
    private void EnterRead()
    {
        lock (_gate)
        {
            while (_writer || _waitingWriters != 0) Monitor.Wait(_gate);
            _readers++;
        }
    }

    private void ExitRead()
    {
        lock (_gate)
            if (--_readers == 0) Monitor.PulseAll(_gate);
    }

    private void EnterWrite()
    {
        lock (_gate)
        {
            _waitingWriters++;
            try
            {
                while (_writer || _readers != 0) Monitor.Wait(_gate);
                _writer = true;
            }
            finally
            {
                _waitingWriters--;
                if (!_writer && _waitingWriters == 0) Monitor.PulseAll(_gate);
            }
        }
    }

    private void ExitWrite()
    {
        lock (_gate)
        {
            _writer = false;
            Monitor.PulseAll(_gate);
        }
    }
    private int GetCell(Vector2 position) => GetRow(position.Y) * Columns + GetColumn(position.X);
    private int GetColumn(double x) => GetCoordinate(x, WorldBounds.Minimum.X, WorldBounds.Maximum.X, Columns);
    private int GetRow(double y) => GetCoordinate(y, WorldBounds.Minimum.Y, WorldBounds.Maximum.Y, Rows);
    private int GetCoordinate(double value, double minimum, double maximum, int count)
    {
        if (value <= minimum) return 0;
        if (value >= maximum) return count - 1;
        return Math.Min((int)((value - minimum) / CellSize), count - 1);
    }

    private struct Node
    {
        internal Vector2 Position;
        internal T Value;
        internal int Cell;
        internal int DenseIndex;
        internal int Previous;
        internal int Next;
    }

    private readonly struct Query
    {
        internal Query(double minX, double minY, double maxX, double maxY, Vector2 center = default, double radiusSquared = -1)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            Center = center;
            RadiusSquared = radiusSquared;
        }

        internal readonly double MinX;
        internal readonly double MinY;
        internal readonly double MaxX;
        internal readonly double MaxY;
        private readonly Vector2 Center;
        private readonly double RadiusSquared;

        internal bool Contains(Vector2 point)
        {
            // Keep exact filtering consistent with cell pruning even when a large center
            // rounds away a small coordinate in the subsequent distance subtraction.
            if (point.X < MinX || point.X > MaxX || point.Y < MinY || point.Y > MaxY) return false;
            if (RadiusSquared < 0) return true;
            double dx = (double)point.X - Center.X;
            double dy = (double)point.Y - Center.Y;
            return dx * dx + dy * dy <= RadiusSquared;
        }
    }
}
