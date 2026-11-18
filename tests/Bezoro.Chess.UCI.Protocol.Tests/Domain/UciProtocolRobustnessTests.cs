using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;
using FluentAssertions;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public class UciProtocolRobustnessTests
{
	[Theory]
	[InlineData("isready")]
	[InlineData("uci")]
	[InlineData("d")]
	public async Task FailedWrite_ShouldRemoveWaitersAndRejectAmbiguousAcknowledgments(string command)
	{
		var transport = Substitute.For<IUciTransport>();
		var waiters = new UciLineWaiterRegistry();
		var module = new UciEngineCommandModule(transport, waiters, _ => { }, new());
		transport.WriteLineAsync(command, Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("write failed")));

		Func<Task> action = command switch
		{
			"isready" => () => module.IsReadyAsync(default),
			"uci" => () => module.UciInitAsync(default),
			_ => async () => { await module.TryGetFenViaDisplayBoardAsync(default); }
		};
		await action.Should().ThrowAsync<IOException>();
		waiters.HasWaiters.Should().BeFalse();

		transport.WriteLineAsync("isready", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			waiters.Notify("readyok");
			return Task.CompletedTask;
		});
		if (command != "d")
			await FluentActions.Awaiting(() => module.IsReadyAsync(default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*restart*");
		else
			await module.IsReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
		waiters.HasWaiters.Should().BeFalse();
	}

	[Fact]
	public async Task InvalidWaiterTimeout_ShouldNotLeakRegistration()
	{
		var waiters = new UciLineWaiterRegistry();
		await FluentActions.Awaiting(() => waiters.WaitForAsync(_ => true, TimeSpan.FromMilliseconds(-2), default))
			.Should().ThrowAsync<ArgumentOutOfRangeException>();
		waiters.HasWaiters.Should().BeFalse();
	}

	[Fact]
	public async Task ImmediateBestMove_ShouldLeaveActivityIdle()
	{
		var transport = Substitute.For<IUciTransport>();
		EngineActivity activity = EngineActivity.Idle;
		var search = new UciSearchCoordinator(transport, value => activity = value, new());
		transport.WriteLineAsync("go depth 1", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			search.HandleBestMove(new("e2e4", "", "bestmove e2e4"));
			return Task.CompletedTask;
		});
		var result = await search.ExecuteSearchAsync(new() { Depth = 1 }, default);
		result.BestMove.Should().Be("e2e4");
		activity.Should().Be(EngineActivity.Idle);
	}

	[Fact]
	public async Task MissingBestMoveAfterCancellation_ShouldRejectFurtherSearchesUntilRestart()
	{
		var transport = Substitute.For<IUciTransport>();
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		var search = new UciSearchCoordinator(transport, _ => { }, new() { SearchStopGracePeriod = TimeSpan.FromMilliseconds(10) });
		using var cancellation = new CancellationTokenSource();
		var pending = search.ExecuteSearchAsync(new() { Infinite = true }, cancellation.Token);
		cancellation.Cancel();
		await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
		await FluentActions.Awaiting(() => search.ExecuteSearchAsync(new() { Depth = 1 }, default))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*restart*");
		// A late response cannot be attributed to a new search.
		search.HandleBestMove(new("e2e4", "", "bestmove e2e4"));
		await FluentActions.Awaiting(() => search.ExecuteSearchAsync(new() { Depth = 1 }, default))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*restart*");

		search.HandleTransportTerminated();
		transport.WriteLineAsync("go depth 1", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			search.HandleBestMove(new("d2d4", "", "bestmove d2d4"));
			return Task.CompletedTask;
		});
		(await search.ExecuteSearchAsync(new() { Depth = 1 }, default)).BestMove.Should().Be("d2d4");
	}
	[Fact]
	public async Task FireAndForgetSearch_ShouldBlockOverlappingSearchUntilBestMoveAfterStop()
	{
		var transport = Substitute.For<IUciTransport>();
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		var search = new UciSearchCoordinator(transport, _ => { }, new());
		await search.ExecuteFireAndForgetAsync(new() { Infinite = true }, default);
		await FluentActions.Awaiting(() => search.ExecuteSearchAsync(new() { Depth = 1 }, default))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in progress*");
		await FluentActions.Awaiting(() => search.ExecuteFireAndForgetAsync(new() { Infinite = true }, default))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in progress*");

		var stopped = search.StopSearchAsync(default);
		stopped.IsCompleted.Should().BeFalse();
		await FluentActions.Awaiting(() => search.ExecuteSearchAsync(new() { Depth = 1 }, default))
			.Should().ThrowAsync<InvalidOperationException>();
		search.HandleBestMove(new("e2e4", "", "bestmove e2e4"));
		await stopped;

		transport.WriteLineAsync("go depth 1", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			search.HandleBestMove(new("d2d4", "", "bestmove d2d4"));
			return Task.CompletedTask;
		});
		(await search.ExecuteSearchAsync(new() { Depth = 1 }, default)).BestMove.Should().Be("d2d4");
	}

	[Fact]
	public async Task StopSearch_ShouldNotOvertakePendingGoWrite()
	{
		var transport = Substitute.For<IUciTransport>();
		var releaseGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stopWritten = false;
		var search = new UciSearchCoordinator(transport, _ => { }, new());
		transport.WriteLineAsync("go infinite", Arg.Any<CancellationToken>()).Returns(releaseGo.Task);
		transport.WriteLineAsync("stop", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			stopWritten = true;
			search.HandleBestMove(new("e2e4", "", "bestmove e2e4"));
			return Task.CompletedTask;
		});
		var start = search.ExecuteFireAndForgetAsync(new() { Infinite = true }, default);
		var stop = search.StopSearchAsync(default);
		stopWritten.Should().BeFalse();
		releaseGo.TrySetResult();
		await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(1));
		stopWritten.Should().BeTrue();
	}

}
