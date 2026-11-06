using System.Globalization;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.API.Types;

public sealed class FenPositionSetupTests
{
	[Fact]
	public void FromFen_ThenBuildFen_PreservesEveryAuthoredFieldAndPiece()
	{
		var fen = Fen.Parse("r3k2r/8/8/3pP3/8/8/8/R3K2R w KQkq d6 0 34")!.Value;
		var setup = FenPositionSetup.FromFen(fen);

		setup.BuildFen().Raw.Should().Be(fen.Raw);
		setup.Pieces[new(4, 4)].Should().Be('P');
		setup.Pieces[new(3, 4)].Should().Be('p');
		setup.ValidateForPlay().IsValid.Should().BeTrue();
	}

	[Fact]
	public void BuildFen_AllowsIncompleteAuthoredBoardsButPlayabilityRejectsThem()
	{
		var setup = new FenPositionSetup();
		setup.BuildFen().Raw.Should().Be("8/8/8/8/8/8/8/8 w - - 0 1");
		var validation = setup.ValidateForPlay();
		validation.IsValid.Should().BeFalse();
		validation.FieldErrors.Should().Contain("Board must contain exactly one white king.")
			.And.Contain("Board must contain exactly one black king.");
	}

	[Fact]
	public void ValidateForPlay_ReportsMultipleInvalidAuthoredFieldsWithoutRepairingThem()
	{
		var setup = new FenPositionSetup { HalfmoveClock = -1, FullmoveNumber = 0, EnPassantTarget = "e4" };
		setup.SetPiece(new(0, 7), 'P');

		var result = setup.ValidateForPlay();

		result.FieldErrors.Should().Contain("Halfmove clock cannot be negative.")
			.And.Contain("Fullmove number must be at least 1.")
			.And.Contain("En passant target must be on rank 3 or rank 6.")
			.And.Contain("White pawns cannot be placed on rank 8.");
		setup.HalfmoveClock.Should().Be(-1);
		setup.FullmoveNumber.Should().Be(0);
		setup.Invoking(value => value.BuildFen()).Should().Throw<ArgumentException>();
	}

	[Fact]
	public void ValidateForPlay_ReportsCastlingPrerequisitesAndMissingKing()
	{
		var setup = FenPositionSetup.FromFen(Fen.Default);
		setup.SetPiece(new(4, 0), '\0');
		setup.SetPiece(new(7, 0), '\0');

		setup.ValidateForPlay().FieldErrors.Should().Contain("Board must contain exactly one white king.")
			.And.Contain("White kingside castling requires a white king on e1.")
			.And.Contain("White kingside castling requires a white rook on h1.");
	}

	[Theory]
	[InlineData("8/8/8/8/8/8/4k3/4K3 w - - 0 1")]
	[InlineData("4k3/8/8/8/8/8/4R3/K7 w - - 0 1")]
	[InlineData("7k/8/8/8/8/P7/PPPPPPPP/7K w - - 0 1")]
	[InlineData("rnbqkbnr/pppppppp/8/8/8/N7/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
	[InlineData("4k3/8/8/4P3/8/8/8/4K3 w - d6 0 2")]
	[InlineData("4k3/8/3N4/3pP3/8/8/8/4K3 w - d6 0 2")]
	[InlineData("4k3/3n4/8/3pP3/8/8/8/4K3 w - d6 0 2")]
	[InlineData("4k3/8/8/3pP3/8/8/8/4K3 w - d6 1 2")]
	public void ValidateForPlay_RejectsTheSameInvalidPositionsAsTheGameFixtures(string raw)
	{
		var setup = FenPositionSetup.FromFen(Fen.Parse(raw)!.Value);
		setup.ValidateForPlay().IsValid.Should().BeFalse();
		setup.Invoking(value => value.CreateMatchSetup()).Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData("4k3/8/8/3p4/8/8/8/4K3 w - d6 0 2")]
	[InlineData("7k/7R/5K2/8/8/8/8/8 b - - 0 1")]
	[InlineData("7k/6Q1/5K2/8/8/8/8/8 b - - 0 1")]
	[InlineData("7k/5Q2/5K2/8/8/8/8/8 b - - 0 1")]
	[InlineData("7k/8/8/8/8/8/8/K7 w - - 0 1")]
	public void ValidateForPlay_AcceptsCheckTerminalAndEnPassantPositions(string raw)
	{
		var setup = FenPositionSetup.FromFen(Fen.Parse(raw)!.Value);
		setup.ValidateForPlay().IsValid.Should().BeTrue();
		setup.CreateMatchSetup().BaseFen.Raw.Should().Be(raw);
	}

	[Fact]
	public void CreateMatchSetup_ExplicitHistoryIsAuthoritativeAndNormalizesItsMoves()
	{
		var emptyAuthoringBoard = new FenPositionSetup();
		var match = emptyAuthoringBoard.CreateMatchSetup(Fen.Default.Raw, [" E2E4 ", "E7E5"]);

		match.BaseFen.Should().Be(Fen.Default);
		match.PlayedMoves.Should().Equal("e2e4", "e7e5");
		match.ResolveCurrentFen().Raw.Should().Be("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2");
	}

	[Theory]
	[InlineData("not a FEN", "e2e4")]
	[InlineData("", "e2e4")]
	[InlineData("8/8/8/8/8/8/4k3/4K3 w - - 0 1", "e1d1")]
	[InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "e2e5")]
	[InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "not-a-move")]
	public void CreateMatchSetup_RejectsInvalidHistoryBaseAndMoves(string raw, string move)
	{
		var setup = FenPositionSetup.FromFen(Fen.Default);
		setup.Invoking(value => value.CreateMatchSetup(raw, [move])).Should().Throw<ArgumentException>();
	}

	[Fact]
	public void BuildFen_RemainsInvariantAcrossCurrentCultures()
	{
		var previous = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
			FenPositionSetup.FromFen(Fen.Default).BuildFen().Raw.Should().Be(Fen.Default.Raw);
		}
		finally { CultureInfo.CurrentCulture = previous; }
	}
}
