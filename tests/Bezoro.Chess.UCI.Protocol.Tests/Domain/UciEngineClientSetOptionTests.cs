using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

[TestSubject(typeof(UciEngineClient))]
public class UciEngineClientSetOptionTests
{
	[Fact]
	public async Task SetOptionAsync_WhenNameIsWhitespace_ShouldThrowArgumentException()
	{
		// Arrange
		var (transport, client) = UciEngineClientTestHelpers.CreateClientWithTransport();
		var ct = CancellationToken.None;

		// Act
		var act = () => client.SetOptionAsync("   ", "ignored", ct);

		// Assert
		await FluentActions.Awaiting(act)
						   .Should()
						   .ThrowAsync<ArgumentException>();
		await transport.DidNotReceiveWithAnyArgs().WriteLineAsync(default!);
	}

	[Fact]
	public async Task SetOptionAsync_WhenValueContainsSpaces_ShouldSendVerbatimValue()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var          client = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var          ct = CancellationToken.None;
		const string VALUE_WITH_SPACES = @"C:\Chess\Table Bases\wdl345";
		var          completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var setOptionTask = client.SetOptionAsync("SyzygyPath", VALUE_WITH_SPACES, ct);
		setOptionTask.IsCompleted.Should().BeFalse("setoption should wait for readyok before completing");
		completeReady();
		await setOptionTask;

		// Assert
		await transport.Received(1).WriteLineAsync($"setoption name SyzygyPath value {VALUE_WITH_SPACES}", ct);
		await transport.Received(1).WriteLineAsync("isready", Arg.Any<CancellationToken>());

		await client.DisposeAsync();
	}

	[Fact]
	public async Task SetOptionAsync_WhenValueIsNull_ShouldSendNameOnlyCommand()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var ct            = CancellationToken.None;
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var setOptionTask = client.SetOptionAsync("Ponder", null, ct);
		setOptionTask.IsCompleted.Should().BeFalse("button/check style setoption calls should also wait for readyok");
		completeReady();
		await setOptionTask;

		// Assert
		await transport.Received(1).WriteLineAsync("setoption name Ponder", ct);
		await transport.Received(1).WriteLineAsync("isready", Arg.Any<CancellationToken>());

		await client.DisposeAsync();
	}

	[Fact]
	public async Task SetOptionAsync_WhenValueIsProvided_ShouldSendNameAndValueCommand()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client        = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
		var ct            = CancellationToken.None;
		var completeReady = UciEngineClientTestHelpers.SetupDelayedReadyResponse(transport, channel);

		// Act
		var setOptionTask = client.SetOptionAsync("Hash", "256", ct);
		setOptionTask.IsCompleted.Should().BeFalse();
		completeReady();
		await setOptionTask;

		// Assert
		await transport.Received(1).WriteLineAsync("setoption name Hash value 256", ct);
		await transport.Received(1).WriteLineAsync("isready", Arg.Any<CancellationToken>());

		await client.DisposeAsync();
	}
}
