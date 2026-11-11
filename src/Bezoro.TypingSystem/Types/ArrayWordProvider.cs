using Bezoro.Core.Types;
using Bezoro.TypingSystem.Abstractions;

namespace Bezoro.TypingSystem.Types;

/// <summary>
///     A mutable word source with atomic consumption of each stored word.
/// </summary>
/// <remarks>
///     Reads and mutations are synchronized. Each stored word is consumed at most once until the provider is cleared.
///     Batch additions commit one word at a time, without holding a lock while enumerating caller code.
/// </remarks>
public sealed class ArrayWordProvider : IWordSource
{
	private readonly SwapbackArray<string> _words;
	private readonly object _gate = new();
	private          int                   _index;

	/// <summary>Creates an empty provider for caller-controlled population or file loading.</summary>
	public ArrayWordProvider() => _words = new();

	/// <summary>
	///     Initializes a new instance of the <see cref="ArrayWordProvider" /> class with the specified words.
	/// </summary>
	/// <param name="words">The collection of words to initialize the provider with.</param>
	public ArrayWordProvider(IEnumerable<string> words)
	{
		if (words is null) throw new ArgumentNullException(nameof(words));
		_words = new(words);
		for (uint i = 0; i < _words.Count; i++)
			if (_words[i] is null) throw new ArgumentException("Words cannot contain null strings.", nameof(words));
	}

	/// <summary>Total stored words, including consumed words.</summary>
	public int WordCount { get { lock (_gate) return (int)_words.Count; } }

	/// <summary>Appends an owned copy of the word.</summary>
	public void AddWord(ReadOnlyMemory<char> word)
	{
		string owned = word.ToString();
		lock (_gate) _words.Add(owned);
	}

	/// <summary>Appends owned copies. Enumeration failure retains words already appended.</summary>
	public void AddWords(IEnumerable<ReadOnlyMemory<char>> words)
	{
		if (words is null) throw new ArgumentNullException(nameof(words));
		foreach (var word in words) AddWord(word);
	}

	/// <summary>Clears all words and resets consumption.</summary>
	public void ClearWords()
	{
		lock (_gate)
		{
			_words.Clear();
			_index = 0;
		}
	}

	/// <summary>Removes the first matching stored word using swapback, which can reorder unread words.</summary>
	public void RemoveWord(ReadOnlyMemory<char> word)
	{
		string owned = word.ToString();
		lock (_gate)
		{
			if (!_words.TryGetIndex(owned, out uint index))
				throw new InvalidOperationException("The specified word was not found.");

			if (index < _index)
			{
				// Keep consumed words before the cursor when swapback moves the unread tail.
				_index--;
				_words[index] = _words[(uint)_index];
				index = (uint)_index;
			}

			_words.TryRemoveAt(index);
		}
	}

	/// <inheritdoc />
	public bool TryGetNextWord(out ReadOnlyMemory<char> word)
	{
		lock (_gate)
		{
			if (_index >= _words.Count)
			{
				word = ReadOnlyMemory<char>.Empty;
				return false;
			}

			word = _words[(uint)_index++].AsMemory();
			return true;
		}
	}
}
