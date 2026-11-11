using System.Collections;
using Bezoro.Logging.Types;

namespace Bezoro.Logging.Internal;

// Mutations publish owned copies. Logging's Contains path needs neither a lock nor an allocation.
internal sealed class ConcurrentCategorySet : ISet<LogCategory>
{
	private readonly object _gate = new();
	private HashSet<LogCategory> _items = new();
	private HashSet<LogCategory> Snapshot => Volatile.Read(ref _items);
	public int Count => Snapshot.Count;
	public bool IsReadOnly => false;
	public bool Contains(LogCategory item) => Snapshot.Contains(item);
	public void CopyTo(LogCategory[] array, int arrayIndex) => Snapshot.CopyTo(array, arrayIndex);
	public IEnumerator<LogCategory> GetEnumerator() => Snapshot.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	void ICollection<LogCategory>.Add(LogCategory item) => Add(item);

	public bool Add(LogCategory item)
	{
		lock (_gate)
		{
			if (_items.Contains(item)) return false;
			var next = new HashSet<LogCategory>(_items) { item };
			Volatile.Write(ref _items, next);
			return true;
		}
	}

	public bool Remove(LogCategory item)
	{
		lock (_gate)
		{
			if (!_items.Contains(item)) return false;
			var next = new HashSet<LogCategory>(_items);
			next.Remove(item);
			Volatile.Write(ref _items, next);
			return true;
		}
	}

	public void Clear()
	{
		lock (_gate)
			if (_items.Count != 0) Volatile.Write(ref _items, new HashSet<LogCategory>());
	}

	public void ExceptWith(IEnumerable<LogCategory> other)
	{ if (ReferenceEquals(other, this)) Clear(); else Update(other, static (set, values) => set.ExceptWith(values)); }
	public void IntersectWith(IEnumerable<LogCategory> other)
	{ if (!ReferenceEquals(other, this)) Update(other, static (set, values) => set.IntersectWith(values)); }
	public void SymmetricExceptWith(IEnumerable<LogCategory> other)
	{ if (ReferenceEquals(other, this)) Clear(); else Update(other, static (set, values) => set.SymmetricExceptWith(values)); }
	public void UnionWith(IEnumerable<LogCategory> other)
	{ if (!ReferenceEquals(other, this)) Update(other, static (set, values) => set.UnionWith(values)); }
	public bool IsProperSubsetOf(IEnumerable<LogCategory> other) => !ReferenceEquals(other, this) && Snapshot.IsProperSubsetOf(other);
	public bool IsProperSupersetOf(IEnumerable<LogCategory> other) => !ReferenceEquals(other, this) && Snapshot.IsProperSupersetOf(other);
	public bool IsSubsetOf(IEnumerable<LogCategory> other) => ReferenceEquals(other, this) || Snapshot.IsSubsetOf(other);
	public bool IsSupersetOf(IEnumerable<LogCategory> other) => ReferenceEquals(other, this) || Snapshot.IsSupersetOf(other);
	public bool Overlaps(IEnumerable<LogCategory> other) => ReferenceEquals(other, this) ? Count != 0 : Snapshot.Overlaps(other);
	public bool SetEquals(IEnumerable<LogCategory> other) => ReferenceEquals(other, this) || Snapshot.SetEquals(other);

	private void Update(IEnumerable<LogCategory> other, Action<HashSet<LogCategory>, HashSet<LogCategory>> update)
	{
		if (other is null) throw new ArgumentNullException(nameof(other));
		// Never enumerate user code while holding the writer gate.
		var values = new HashSet<LogCategory>(other);
		lock (_gate)
		{
			var next = new HashSet<LogCategory>(_items);
			update(next, values);
			Volatile.Write(ref _items, next);
		}
	}
}
