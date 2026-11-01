using Bezoro.TypingSystem.Abstractions;
using Bezoro.TypingSystem.Extensions;
using Bezoro.TypingSystem.Types;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.TypingSystem.Tests.Types;

[TestSubject(typeof(ArrayWordProvider))]
public class ArrayWordProviderTests
{
	[Fact]
	public void AddWord_WhenCalled_ShouldIncreaseWordCount()
	{
		var provider = new ArrayWordProvider(["one"]);

		provider.AddWord("two".AsMemory());

		provider.WordCount.Should().Be(2);
	}

	[Fact]
	public void AddWords_WhenCalled_ShouldAddAllWords()
	{
		var provider = new ArrayWordProvider(["one"]);

		provider.AddWords(["two".AsMemory(), "three".AsMemory()]);

		provider.WordCount.Should().Be(3);
	}

	[Fact]
	public void LoadWordsFromFile_WhenFileContainsWords_ShouldAppendWords()
	{
		string filePath = Path.GetTempFileName();
		File.WriteAllLines(filePath, ["two", "three"]);

		try
		{
			var provider = new ArrayWordProvider(["one"]);
			provider.LoadWordsFromFile(filePath);

			provider.WordCount.Should().Be(3);
			Drain(provider).Should().Equal("one", "two", "three");
		}
		finally
		{
			File.Delete(filePath);
		}
	}

	[Fact]
	public void LoadWordsFromFile_WhenProviderIsNull_ShouldThrowArgumentNullException()
	{
		ArrayWordProvider provider = null!;

		var action = () => provider.LoadWordsFromFile("words.txt");

		action.Should().Throw<ArgumentNullException>()
			.WithParameterName("provider");
	}

	[Fact]
	public void LoadWordsFromFile_WhenFilePathIsNull_ShouldThrowArgumentNullException()
	{
		var provider = new ArrayWordProvider(["one"]);

		var action = () => provider.LoadWordsFromFile(null!);

		action.Should().Throw<ArgumentNullException>()
			.WithParameterName("filePath");
	}

	[Fact]
	public void LoadWordsFromFile_WhenFileDoesNotExist_ShouldLeaveProviderUnchanged()
	{
		var provider = new ArrayWordProvider(["one", "two"]);
		_ = provider.TryGetNextWord(out _);
		var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");

		var action = () => provider.LoadWordsFromFile(filePath);

		action.Should().Throw<FileNotFoundException>();
		provider.WordCount.Should().Be(2);
		Drain(provider).Should().Equal("two");
	}

	[Fact]
	public void ClearWords_WhenCalled_ShouldResetWordCountAndReadIndex()
	{
		var provider = new ArrayWordProvider(["one", "two"]);
		_ = provider.TryGetNextWord(out _);
		provider.ClearWords();
		provider.AddWord("three".AsMemory());

		provider.TryGetNextWord(out var word).Should().BeTrue();

		word.ToString().Should().Be("three");
		provider.WordCount.Should().Be(1);
		provider.TryGetNextWord(out _).Should().BeFalse();
	}

	[Fact]
	public void Constructor_WhenWordsAreEmpty_ShouldSupportLaterPopulation()
	{
		var provider = new ArrayWordProvider([]);
		provider.TryGetNextWord(out _).Should().BeFalse();
		provider.AddWord("first".AsMemory());
		provider.TryGetNextWord(out var word).Should().BeTrue();
		word.ToString().Should().Be("first");
	}

	[Fact]
	public void EmptyWord_CanBeRemovedAfterInsertion()
	{
		var provider = new ArrayWordProvider();
		provider.AddWord(ReadOnlyMemory<char>.Empty);
		provider.RemoveWord(ReadOnlyMemory<char>.Empty);
		provider.WordCount.Should().Be(0);
	}

	[Fact]
	public void Constructor_RejectsNullWordEntries()
	{
		Action action = () => new ArrayWordProvider(new[] { "valid", null! });
		action.Should().Throw<ArgumentException>().WithParameterName("words");
	}

	[Fact]
	public void Constructor_WhenWordsAreNull_ShouldThrowArgumentNullException()
	{
		IEnumerable<string> words = null!;

		Action action = () => _ = new ArrayWordProvider(words);

		action.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void TryGetNextWord_WhenWordsExist_ShouldConsumeInInsertionOrder()
	{
		IWordSource source = new ArrayWordProvider(["one", "two"]);

		source.TryGetNextWord(out var first).Should().BeTrue();
		source.TryGetNextWord(out var second).Should().BeTrue();

		first.ToString().Should().Be("one");
		second.ToString().Should().Be("two");
	}

	[Fact]
	public void TryGetNextWord_WhenExhausted_ShouldReturnFalseAndEmptyMemory()
	{
		IWordSource source = new ArrayWordProvider(["one"]);
		_ = source.TryGetNextWord(out _);

		source.TryGetNextWord(out var word).Should().BeFalse();
		word.Should().Be(ReadOnlyMemory<char>.Empty);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(10_000)]
	public async Task TryGetNextWord_WhenConsumedConcurrently_ShouldReturnEachWordExactlyOnce(int wordCount)
	{
		const int consumerCount = 8;
		var words = Enumerable.Range(0, wordCount).Select(index => $"word-{index}").ToArray();
		IWordSource source = new ArrayWordProvider(words);
		using var start = new Barrier(consumerCount);

		var consumers = Enumerable.Range(0, consumerCount).Select(_ => Task.Factory.StartNew(() =>
		{
			if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
				throw new TimeoutException("Concurrent consumers did not reach the start barrier.");

			var consumed = new List<string>();
			while (source.TryGetNextWord(out var word)) consumed.Add(word.ToString());

			source.TryGetNextWord(out var exhausted).Should().BeFalse();
			exhausted.Should().Be(ReadOnlyMemory<char>.Empty);
			return consumed;
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

		var batches = await Task.WhenAll(consumers);
		var consumedWords = batches.SelectMany(batch => batch).ToArray();
		consumedWords.Should().HaveCount(wordCount);
		consumedWords.Order(StringComparer.Ordinal).Should().Equal(words.Order(StringComparer.Ordinal));
	}

	[Fact]
	public void TryGetNextWord_WhenWordIsAddedAfterRepeatedExhaustion_ShouldConsumeAppendedWord()
	{
		var provider = new ArrayWordProvider(["one"]);
		IWordSource source = provider;
		_ = source.TryGetNextWord(out _);
		for (var attempt = 0; attempt < 10; attempt++)
		{
			source.TryGetNextWord(out var exhausted).Should().BeFalse();
			exhausted.Should().Be(ReadOnlyMemory<char>.Empty);
		}

		provider.AddWord("two".AsMemory());

		source.TryGetNextWord(out var word).Should().BeTrue();
		word.ToString().Should().Be("two");
		source.TryGetNextWord(out _).Should().BeFalse();
	}

	[Fact]
	public void RemoveWord_WhenWordDoesNotExist_ShouldThrowInvalidOperationException()
	{
		var provider = new ArrayWordProvider(["one"]);

		var action = () => provider.RemoveWord("missing".AsMemory());

		action.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public void WordCount_WhenWordsAreAddedAndRemoved_ShouldReflectCurrentCount()
	{
		var provider = new ArrayWordProvider(["one", "two"]);
		provider.AddWord("three".AsMemory());
		provider.RemoveWord("two".AsMemory());

		provider.WordCount.Should().Be(2);
	}

	private static IEnumerable<string> Drain(IWordSource source)
	{
		while (source.TryGetNextWord(out var word)) yield return word.ToString();
	}
}
