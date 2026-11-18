using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

[TestSubject(typeof(UciEngineClient))]
public class UciEngineClientIsReadyTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task AbandonedAcknowledgment_RejectsQueuedAndLateChecksUntilRestart(bool cancelCaller)
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		await using var client = new UciEngineClient(transport, new() { ReadyTimeout = TimeSpan.FromMilliseconds(150) });
		var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reply = true;
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			if ((string)call[0] == "uci") channel.Writer.TryWrite("uciok");
			if ((string)call[0] == "isready")
			{
				if (reply) channel.Writer.TryWrite("readyok");
				else issued.TrySetResult();
			}
			return Task.CompletedTask;
		});
		await client.StartAsync();
		reply = false;
		using var cts = new CancellationTokenSource();
		var abandoned = client.IsReadyAsync(cts.Token);
		await issued.Task.WaitAsync(TestConstants.DefaultTimeout);
		var queued = client.IsReadyAsync(default);
		if (cancelCaller) cts.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
		await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
		await channel.Writer.WriteAsync("readyok");
		await Assert.ThrowsAsync<InvalidOperationException>(() => client.IsReadyAsync(default));
		await transport.Received(2).WriteLineAsync("isready", Arg.Any<CancellationToken>());

		await client.StopAsync();
		reply = true;
		await client.StartAsync();
		await client.IsReadyAsync(default);
	}

	[Fact]
	public async Task ReadyTimeout_CancelsBackpressuredTransportWrite()
	{
		var (transport, _) = UciEngineClientTestHelpers.CreateMockTransport();
		await using var client = new UciEngineClient(transport, new() { ReadyTimeout = TimeSpan.FromMilliseconds(50) });
		transport.WriteLineAsync("isready", Arg.Any<CancellationToken>())
			.Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, (CancellationToken)call[1]));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.IsReadyAsync(default).WaitAsync(TestConstants.DefaultTimeout));
	}

	[Fact(Timeout = 4000)]
	public async Task IsReadyAsync_WhenCalledConcurrently_ShouldSerializeReadyChecks()
	{
		// Arrange
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);

		var firstReadyResponseGate  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondReadyResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstCommandIssued      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondCommandIssued     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var readyCommandCount       = 0;

		transport.ClearReceivedCalls();
		transport.When(x => x.WriteLineAsync("isready", Arg.Any<CancellationToken>()))
				 .Do(async _ =>
					 {
						 int invocation = Interlocked.Increment(ref readyCommandCount);
						 if (invocation == 1)
						 {
							 firstCommandIssued.TrySetResult();
							 await firstReadyResponseGate.Task;
						 }
						 else
						 {
							 secondCommandIssued.TrySetResult();
							 await secondReadyResponseGate.Task;
						 }

						 await channel.Writer.WriteAsync("readyok");
					 }
				 );

		// Act
		var firstReadyTask  = client.IsReadyAsync(CancellationToken.None);
		var secondReadyTask = client.IsReadyAsync(CancellationToken.None);

		// Assert
		await firstCommandIssued.Task.WaitAsync(TestConstants.DefaultTimeout);
		await Task.Delay(TestConstants.ShortDelay);

		readyCommandCount.Should().Be(1, "the second ready check should wait for the first response");
		secondReadyTask.IsCompleted.Should().BeFalse();

		firstReadyResponseGate.TrySetResult();
		await firstReadyTask.WaitAsync(TestConstants.DefaultTimeout);
		await secondCommandIssued.Task.WaitAsync(TestConstants.DefaultTimeout);

		readyCommandCount.Should().Be(2, "each concurrent call should issue its own ready check");

		secondReadyResponseGate.TrySetResult();
		await secondReadyTask.WaitAsync(TestConstants.DefaultTimeout);
	}

	[Fact]
	public async Task IsReadyAsync_WhenCancelled_ShouldThrowOperationCanceledException()
	{
		// Arrange
		var (_, client) = UciEngineClientTestHelpers.CreateClientWithTransport();
		var cts = new CancellationTokenSource(TestConstants.CancellationTimeout);

		// Act + Assert
		await FluentActions
			  .Awaiting(() => client.IsReadyAsync(cts.Token))
			  .Should()
			  .ThrowAsync<OperationCanceledException>("operation should be cancelled");
	}
}
