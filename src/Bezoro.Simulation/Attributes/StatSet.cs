using System.Threading;

namespace Bezoro.Simulation.Attributes;

/// <summary>
/// Owns a fixed set of attributes and a bounded reusable modifier pool. Mutations and cached reads require
/// an owning thread or external serialization. Different instances may run independently on different threads.
/// </summary>
public sealed class StatSet
{
    private static long _lastOwner;
    private readonly long _owner;
    private readonly Dictionary<StatId, int> _indices;
    private readonly StatDefinition[] _definitions;
    private readonly StatEvaluation[] _cached;
    private readonly bool[] _dirty;
    private readonly int[] _heads;
    private readonly int[] _tails;
    private readonly int[] _counts;
    private readonly ModifierSlot[] _slots;
    private int _freeHead;

    /// <summary>Copies definitions and allocates all modifier storage. Identifiers must be unique.</summary>
    public StatSet(ReadOnlySpan<StatDefinition> definitions, int modifierCapacity)
    {
        if (modifierCapacity < 0) throw new ArgumentOutOfRangeException(nameof(modifierCapacity));
        _indices = new(definitions.Length);
        for (int i = 0; i < definitions.Length; i++)
        {
            if (!definitions[i].Id.IsValid)
                throw new ArgumentException("Every stat definition must be initialized.", nameof(definitions));
            if (_indices.ContainsKey(definitions[i].Id))
                throw new ArgumentException("Stat identifiers must be unique.", nameof(definitions));
            _indices.Add(definitions[i].Id, i);
        }

        _definitions = definitions.ToArray();
        _cached = new StatEvaluation[definitions.Length];
        _dirty = new bool[definitions.Length];
        _heads = new int[definitions.Length];
        _tails = new int[definitions.Length];
        _counts = new int[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            _dirty[i] = true;
            _heads[i] = _tails[i] = -1;
        }

        _slots = new ModifierSlot[modifierCapacity];
        for (int i = 0; i < modifierCapacity; i++)
        {
            _slots[i].Generation = 1;
            _slots[i].Next = i + 1 < modifierCapacity ? i + 1 : -1;
        }
        _freeHead = modifierCapacity == 0 ? -1 : 0;
        AvailableModifierSlots = modifierCapacity;
        _owner = AllocateOwner();
    }

    /// <summary>Gets the fixed number of attributes.</summary>
    public int StatCount => _definitions.Length;

    /// <summary>Gets the allocated maximum number of live modifiers.</summary>
    public int ModifierCapacity => _slots.Length;

    /// <summary>Gets the number of live modifiers across all attributes.</summary>
    public int ModifierCount { get; private set; }

    /// <summary>Gets reusable slots. A slot is retired instead of wrapping its generation after uint.MaxValue uses.</summary>
    public int AvailableModifierSlots { get; private set; }

    /// <summary>Gets whether an attribute is defined.</summary>
    public bool Contains(StatId id) => _indices.ContainsKey(id);

    /// <summary>Gets the current definition of an attribute.</summary>
    public StatDefinition GetDefinition(StatId id) => _definitions[FindStat(id)];

    /// <summary>Replaces an existing attribute's definition and invalidates only its cached evaluation.</summary>
    public void SetDefinition(StatDefinition definition)
    {
        int index = FindStat(definition.Id);
        _definitions[index] = definition;
        _dirty[index] = true;
    }

    /// <summary>Changes an attribute's base value, preserving its output bounds.</summary>
    public void SetBaseValue(StatId id, double baseValue)
    {
        int index = FindStat(id);
        var replacement = _definitions[index].WithBaseValue(baseValue);
        _definitions[index] = replacement;
        _dirty[index] = true;
    }

    /// <summary>Gets the cached value, recomputing only when this attribute changed.</summary>
    public double GetValue(StatId id) => Evaluate(id).Value;

    /// <summary>Gets the cached result, recomputing only when this attribute changed.</summary>
    public StatEvaluation Evaluate(StatId id)
    {
        int index = FindStat(id);
        if (!_dirty[index]) return _cached[index];
        var accumulator = new StatCalculator.Accumulator();
        for (int slot = _heads[index]; slot >= 0; slot = _slots[slot].Next)
            accumulator.Add(_slots[slot].Modifier);
        var result = accumulator.Complete(_definitions[index]);
        _cached[index] = result;
        _dirty[index] = false;
        return result;
    }

    /// <summary>Adds a modifier in insertion order. Throws if no reusable slot remains.</summary>
    public StatModifierHandle AddModifier(StatId id, StatModifier modifier)
    {
        if (!TryAddModifier(id, modifier, out var handle))
            throw new InvalidOperationException("No modifier slot is available.");
        return handle;
    }

    /// <summary>Adds a modifier, returning false only when the fixed pool has no reusable slot.</summary>
    public bool TryAddModifier(StatId id, StatModifier modifier, out StatModifierHandle handle)
    {
        int index = FindStat(id);
        if (_freeHead < 0)
        {
            handle = default;
            return false;
        }
        int slot = _freeHead;
        ref var entry = ref _slots[slot];
        _freeHead = entry.Next;
        entry.Modifier = modifier;
        entry.StatIndex = index;
        entry.Previous = _tails[index];
        entry.Next = -1;
        entry.Active = true;
        if (_tails[index] < 0) _heads[index] = slot;
        else _slots[_tails[index]].Next = slot;
        _tails[index] = slot;
        _counts[index]++;
        ModifierCount++;
        AvailableModifierSlots--;
        _dirty[index] = true;
        handle = new(_owner, slot, entry.Generation);
        return true;
    }

    /// <summary>Changes a live modifier without changing its insertion-order position.</summary>
    public bool TryUpdateModifier(StatModifierHandle handle, StatModifier modifier)
    {
        if (!IsLive(handle)) return false;
        ref var entry = ref _slots[handle.Slot];
        entry.Modifier = modifier;
        _dirty[entry.StatIndex] = true;
        return true;
    }

    /// <summary>Reads a live modifier. Stale, foreign, and default handles return false.</summary>
    public bool TryGetModifier(StatModifierHandle handle, out StatId stat, out StatModifier modifier)
    {
        if (!IsLive(handle))
        {
            stat = default;
            modifier = default;
            return false;
        }
        ref var entry = ref _slots[handle.Slot];
        stat = _definitions[entry.StatIndex].Id;
        modifier = entry.Modifier;
        return true;
    }

    /// <summary>Removes a live modifier. Stale, foreign, and default handles return false.</summary>
    public bool TryRemoveModifier(StatModifierHandle handle)
    {
        if (!IsLive(handle)) return false;
        RemoveSlot(handle.Slot);
        return true;
    }

    /// <summary>Gets the number of live modifiers affecting an attribute.</summary>
    public int GetModifierCount(StatId id) => _counts[FindStat(id)];

    /// <summary>Copies modifiers in insertion order. An undersized destination is rejected before any write.</summary>
    public int CopyModifiersTo(StatId id, Span<StatModifier> destination)
    {
        int index = FindStat(id);
        if (destination.Length < _counts[index]) throw new ArgumentException("The destination is too small.", nameof(destination));
        int copied = 0;
        for (int slot = _heads[index]; slot >= 0; slot = _slots[slot].Next)
            destination[copied++] = _slots[slot].Modifier;
        return copied;
    }

    /// <summary>Removes all modifiers from one attribute and invalidates their handles.</summary>
    public int ClearModifiers(StatId id)
    {
        int index = FindStat(id);
        int removed = _counts[index];
        while (_heads[index] >= 0) RemoveSlot(_heads[index]);
        return removed;
    }

    /// <summary>Removes all modifiers and invalidates their handles, preserving definitions and base values.</summary>
    public int ClearModifiers()
    {
        int removed = ModifierCount;
        for (int index = 0; index < _heads.Length; index++)
            while (_heads[index] >= 0) RemoveSlot(_heads[index]);
        return removed;
    }

    private int FindStat(StatId id)
    {
        if (!id.IsValid) throw new ArgumentException("A stat identifier must be initialized.", nameof(id));
        if (!_indices.TryGetValue(id, out int index))
            throw new KeyNotFoundException("The attribute is not defined in this set.");
        return index;
    }

    private bool IsLive(StatModifierHandle handle) => handle.Owner == _owner &&
        unchecked((uint)handle.Slot) < (uint)_slots.Length && _slots[handle.Slot].Active &&
        _slots[handle.Slot].Generation == handle.Generation;

    private void RemoveSlot(int slot)
    {
        ref var entry = ref _slots[slot];
        int index = entry.StatIndex;
        if (entry.Previous >= 0) _slots[entry.Previous].Next = entry.Next;
        else _heads[index] = entry.Next;
        if (entry.Next >= 0) _slots[entry.Next].Previous = entry.Previous;
        else _tails[index] = entry.Previous;
        entry.Active = false;
        entry.Modifier = default;
        entry.Previous = -1;
        _counts[index]--;
        ModifierCount--;
        _dirty[index] = true;
        if (entry.Generation == uint.MaxValue) entry.Next = -1;
        else
        {
            entry.Generation++;
            entry.Next = _freeHead;
            _freeHead = slot;
            AvailableModifierSlots++;
        }
    }

    private static long AllocateOwner()
    {
        while (true)
        {
            long previous = Interlocked.Read(ref _lastOwner);
            if (previous == long.MaxValue) throw new InvalidOperationException("The stat-set owner identifier space is exhausted.");
            if (Interlocked.CompareExchange(ref _lastOwner, previous + 1, previous) == previous) return previous + 1;
        }
    }

    private struct ModifierSlot
    {
        internal StatModifier Modifier;
        internal int StatIndex;
        internal int Previous;
        internal int Next;
        internal uint Generation;
        internal bool Active;
    }
}
