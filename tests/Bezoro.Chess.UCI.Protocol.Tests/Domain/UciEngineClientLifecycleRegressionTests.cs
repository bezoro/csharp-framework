using System.Threading.Channels;
using FluentAssertions;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public class UciEngineClientLifecycleRegressionTests
{
	[Fact]
	public async Task ThrowingReaderCancellationCallback_DoesNotSkipTransportShutdown()
	{
		var (transport, client) = CreateResponsiveClient(token =>
			token.Register(static () => throw new InvalidOperationException("Cancellation callback failed.")));
		await using var disposable = client;
		await client.StartAsync();

		var error = await Assert.ThrowsAsync<AggregateException>(() => client.StopAsync());

		Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
		await transport.Received(1).StopAsync(Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task StopCancellationAfterAdmission_ShouldCompleteTransportShutdown()
	{
		var (transport, client) = CreateResponsiveClient();
		await using var disposable = client;
		await client.StartAsync();
		using var caller = new CancellationTokenSource();
		bool stopped = false;
		transport.StopAsync(Arg.Any<CancellationToken>()).Returns(call =>
		{
			caller.Cancel();
			call.Arg<CancellationToken>().ThrowIfCancellationRequested();
			stopped = true;
			return Task.CompletedTask;
		});
		await FluentActions.Awaiting(() => client.StopAsync(caller.Token)).Should().ThrowAsync<OperationCanceledException>();
		stopped.Should().BeTrue();
	}

	[Fact]
	public async Task CancelingStartupTokenAfterSuccess_ShouldKeepReaderAlive()
	{
		var (_, client) = CreateResponsiveClient();
		await using var disposable = client;
		using var startupCancellation = new CancellationTokenSource();
		await client.StartAsync(startupCancellation.Token);
		startupCancellation.Cancel();
		await client.IsReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task RestartDuringStop_ShouldWaitForTransportShutdown()
	{
		var (transport, client) = CreateResponsiveClient();
		await using var disposable = client;
		await client.StartAsync();
		var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		transport.StopAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
		{
			stopEntered.TrySetResult();
			await releaseStop.Task;
		});

		var stop = client.StopAsync();
		await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
		var restart = client.StartAsync();
		restart.IsCompleted.Should().BeFalse();
		await transport.Received(1).StartAsync(Arg.Any<CancellationToken>());
		releaseStop.TrySetResult();
		await Task.WhenAll(stop, restart).WaitAsync(TimeSpan.FromSeconds(1));
		await transport.Received(2).StartAsync(Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task DisposeDuringStartup_ShouldWaitAndStopTheStartedTransport()
	{
		var (transport, client) = CreateResponsiveClient();
		var enteredStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		transport.StartAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
		{
			enteredStart.TrySetResult();
			await releaseStart.Task;
		});

		var start = client.StartAsync();
		await enteredStart.Task.WaitAsync(TimeSpan.FromSeconds(1));
		var dispose = client.DisposeAsync().AsTask();
		dispose.IsCompleted.Should().BeFalse();
		await transport.DidNotReceive().StopAsync(Arg.Any<CancellationToken>());
		releaseStart.TrySetResult();
		await Task.WhenAll(start, dispose).WaitAsync(TimeSpan.FromSeconds(1));
		await transport.Received(1).StopAsync(Arg.Any<CancellationToken>());
		await client.DisposeAsync();
		await transport.Received(1).DisposeAsync();
		await FluentActions.Awaiting(() => client.StartAsync()).Should().ThrowAsync<ObjectDisposedException>();
	}

	private static (IUciTransport transport, UciEngineClient client) CreateResponsiveClient(Action<CancellationToken>? onRead = null)
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		if (onRead is not null)
			transport.ReadLinesAsync(Arg.Any<CancellationToken>()).Returns(call =>
			{
				var token = call.Arg<CancellationToken>();
				onRead(token);
				return UciEngineClientTestHelpers.StreamFromChannel(channel.Reader, token);
			});
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			string command = (string)call[0];
			if (command == "uci") channel.Writer.TryWrite("uciok");
			if (command == "isready") channel.Writer.TryWrite("readyok");
			return Task.CompletedTask;
		});
		return (transport, new UciEngineClient(transport, new() { ReadyTimeout = TimeSpan.FromMilliseconds(250) }));
	}
}
