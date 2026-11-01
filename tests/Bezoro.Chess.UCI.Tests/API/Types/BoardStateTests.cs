using Bezoro.Chess.UCI.API.Types;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Tests.API.Types;

[TestSubject(typeof(BoardState))]
public class BoardStateTests
{
	[Fact]
	public void FromFen_ExposesAnImmutablePositionCollection()
	{
		var board = BoardState.FromFen(Fen.Default)!.Value;
		var writable = (ICollection<Position>)board.Positions;
		writable.IsReadOnly.Should().BeTrue();
		Action clear = writable.Clear;
		clear.Should().Throw<NotSupportedException>();
		board.Positions.Should().HaveCount(64);
		board.TryGetPieceAt("e1", out var king).Should().BeTrue();
		king.Should().NotBeNull();
	}
	[Fact]
	public void FromFen_WhenInvalidFen_ShouldThrowArgumentException()
	{
		var invalidFen = Fen.Empty();

		var boardState = BoardState.FromFen(invalidFen);

		boardState.Should().BeNull();
	}

	[Fact]
	public void FromFen_WhenValidFen_ShouldReturnValidObject()
	{
		var board = BoardState.FromFen(Fen.Default);

		board.Should().NotBeNull();
	}
}
