using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

[TestSubject(typeof(UciEngineClient))]
public class UciEngineClientGetLegalMovesViaGoPerft1Tests
{
	[Fact]
	public async Task PerftCapture_IgnoresThrowingPublicObserversAndEndsExactlyAtReadyOk()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		await using var client = new UciEngineClient(transport);
		transport.WriteLineAsync("uci", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			channel.Writer.TryWrite("uciok");
			return Task.CompletedTask;
		});
		var readyCount = 0;
		transport.WriteLineAsync("isready", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			channel.Writer.TryWrite("readyok");
			if (++readyCount > 1) channel.Writer.TryWrite("b2b3");
			return Task.CompletedTask;
		});
		transport.WriteLineAsync("go perft 1", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			channel.Writer.TryWrite("e2e4: 1");
			return Task.CompletedTask;
		});
		client.RawLineReceived += _ => throw new InvalidOperationException();
		await client.StartAsync();
		var moves = await client.GetLegalMovesViaPerftAsync(default);
		Assert.Equal(new[] { "e2e4" }, moves.ToArray());
	}

	[Fact]
	public async Task GetLegalMovesViaPerftAsync_WhenCalled_ShouldFilterInvalidMoves()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var perftTask = client.GetLegalMovesViaPerftAsync(CancellationToken.None);
		await channel.Writer.WriteAsync("e2e4 ; bad");
		await Task.Delay(TestConstants.MediumDelay);
		completeReady();
		var moves = await perftTask;

		// Assert
		moves.Should().Contain("e2e4", "valid move should be included");
		moves.Should().NotContain("bad", "invalid move notation should be filtered out");
	}

	[Fact]
	public async Task GetLegalMovesViaPerftAsync_WhenCalled_ShouldHandleMixedValidAndInvalidMoves()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var perftTask = client.GetLegalMovesViaPerftAsync(CancellationToken.None);
		await channel.Writer.WriteAsync("e2e4, e7e5 ; bad | a7a8q : h7h8N");
		await Task.Delay(TestConstants.MediumDelay);
		completeReady();
		var moves = await perftTask;

		// Assert
		moves.Should().Contain("e2e4",  "e2e4 should be parsed");
		moves.Should().Contain("e7e5",  "e7e5 should be parsed");
		moves.Should().Contain("a7a8q", "a7a8q should be parsed");
		moves.Should().Contain("h7h8N", "h7h8N should be parsed");
		moves.Should().NotContain("bad", "invalid move should be filtered");
	}

	[Fact]
	public async Task GetLegalMovesViaPerftAsync_WhenCalled_ShouldParseMultipleValidMoves()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var perftTask = client.GetLegalMovesViaPerftAsync(CancellationToken.None);
		await channel.Writer.WriteAsync("e2e4, e7e5");
		await Task.Delay(TestConstants.MediumDelay);
		completeReady();
		var moves = await perftTask;

		// Assert
		moves.Should().Contain("e2e4", "e2e4 should be parsed");
		moves.Should().Contain("e7e5", "e7e5 should be parsed");
	}

	[Fact]
	public async Task GetLegalMovesViaPerftAsync_WhenCalled_ShouldParsePromotionMoves()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var perftTask = client.GetLegalMovesViaPerftAsync(CancellationToken.None);
		await channel.Writer.WriteAsync("a7a8q : h7h8N");
		await Task.Delay(TestConstants.MediumDelay);
		completeReady();
		var moves = await perftTask;

		// Assert
		moves.Should().Contain("a7a8q", "a7a8q should be parsed as a valid promotion move");
		moves.Should().Contain("h7h8N", "h7h8N should be parsed as a valid promotion move");
	}

	[Fact]
	public async Task GetLegalMovesViaPerftAsync_WhenCalled_ShouldParseValidMove()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var perftTask = client.GetLegalMovesViaPerftAsync(CancellationToken.None);
		await channel.Writer.WriteAsync("e2e4");
		await Task.Delay(TestConstants.MediumDelay);
		completeReady();
		var moves = await perftTask;

		// Assert
		moves.Should().Contain("e2e4", "e2e4 should be parsed as a valid move");
	}

	[Fact]
	public async Task GetLegalMovesViaPerftAsync_WhenCalled_ShouldSendGoPerftCommand()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var perftTask = client.GetLegalMovesViaPerftAsync(CancellationToken.None);
		await channel.Writer.WriteAsync("e2e4");
		await Task.Delay(TestConstants.MediumDelay);
		completeReady();
		await perftTask;

		// Assert
		await transport.Received().WriteLineAsync("go perft 1", Arg.Any<CancellationToken>());
	}
}
