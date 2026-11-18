using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit.Abstractions;
using static Bezoro.Chess.UCI.Protocol.Tests.Domain.ProcessUciTransportTestHelpers;
using static Bezoro.Chess.UCI.Protocol.Tests.TestHelpers.TestDataBuilders;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

[TestSubject(typeof(ProcessUciTransport))]
[Trait("Category", "Integration")]
[Collection("Stockfish")]
public class ProcessUciTransportLifecycleTests(StockfishFixture fixture, ITestOutputHelper output)
	: IntegrationTestBase(fixture, output)
{
	[Fact]
	public async Task StopWithoutQuit_ShouldDeliverEofAndAllowNormalChildExit()
	{
		await using var process = ProcessUciTransportTestHelpers.ExitOnInputProcess()
			.WithQuitGracePeriod(TimeSpan.FromSeconds(3)).Build();
		int? code = null;
		process.Exited += (exitCode, _) => code = exitCode;
		await process.StartAsync();
		await process.StopAsync();
		code.Should().Be(0);
	}

	[Fact]
	public void Constructor_WhenArgsContainsNull_ShouldThrowArgumentException()
	{
		Log("Starting test: Constructor_WhenArgsContainsNull_ShouldThrowArgumentException");
		string[] argsWithNull = ["arg1", null!, "arg2"];

		var act = () => _ = new ProcessUciTransport("any/nonempty/path", argsWithNull);

		act.Should().Throw<ArgumentException>()
		   .WithMessage("*cannot contain null values*");
	}

	[Fact]
	public void Constructor_WhenDefault_ShouldHaveInitialStateCorrect()
	{
		Log("Starting test: Constructor_WhenDefault_ShouldHaveInitialStateCorrect");
		var process = Transport().WithPath("any/nonempty/path").Build();

		process.Status.Should().Be(TransportStatus.Created);
		process.IsStarted.Should().BeFalse();
		process.BackpressureEvents.Should().Be(0);
		process.LinesRead.Should().Be(0);
		process.LinesWritten.Should().Be(0);
	}

	[Fact]
	public void Constructor_WhenEmptyPath_ShouldThrowArgumentException()
	{
		Log("Starting test: Constructor_WhenEmptyPath_ShouldThrowArgumentException");
		var act = () => _ = new ProcessUciTransport("");

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructor_WhenFourParameterOverload_ShouldWorkCorrectly()
	{
		Log("Starting test: Constructor_WhenFourParameterOverload_ShouldWorkCorrectly");
		var process = Transport()
					  .WithPath("any/nonempty/path")
					  .WithArguments("arg1", "arg2")
					  .WithWorkingDirectory(Environment.CurrentDirectory)
					  .WithChannelCapacity(512)
					  .Build();

		process.Should().NotBeNull();
		process.Status.Should().Be(TransportStatus.Created);
		process.IsStarted.Should().BeFalse();
	}

	[Fact]
	public void Constructor_WhenInvalidChannelCapacity_ShouldThrowArgumentOutOfRangeException()
	{
		Log("Starting test: Constructor_WhenInvalidChannelCapacity_ShouldThrowArgumentOutOfRangeException");
		Action act = () => _ = Transport()
							   .WithPath("any/nonempty/path")
							   .WithChannelCapacity(0)
							   .Build();

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Constructor_WhenInvalidNewLine_ShouldThrowArgumentException()
	{
		Log("Starting test: Constructor_WhenInvalidNewLine_ShouldThrowArgumentException");
		var    invalidOptions = new ProcessUciTransportOptions { NewLine = "" };
		Action act            = () => _ = new ProcessUciTransport("any/nonempty/path", null, null, invalidOptions);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructor_WhenNullPath_ShouldThrowArgumentException()
	{
		Log("Starting test: Constructor_WhenNullPath_ShouldThrowArgumentException");
		var act = () => _ = new ProcessUciTransport(null!);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructor_WhenWhitespacePath_ShouldThrowArgumentException()
	{
		Log("Starting test: Constructor_WhenWhitespacePath_ShouldThrowArgumentException");
		var act = () => _ = new ProcessUciTransport("   ");

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public async Task DisposeAsync_AfterStop_WhenCalled_ShouldNotThrow()
	{
		Log("Starting test: DisposeAsync_AfterStop_ShouldNotThrow");
		var process = Transport().Build();
		await process.StartAsync();
		await process.StopAsync();

		var act = async () => await process.DisposeAsync();

		await act.Should().NotThrowAsync();
		process.Status.Should().Be(TransportStatus.Disposed);
	}

	[Fact]
	public async Task DisposeAsync_WhenCalledConcurrently_ShouldBeIdempotent()
	{
		Log("Starting test: DisposeAsync_WhenCalledConcurrently_ShouldBeIdempotent");
		var process = Transport().Build();
		await process.StartAsync();

		var t1 = process.DisposeAsync();
		var t2 = process.DisposeAsync();
		var t3 = process.DisposeAsync();

		await Task.WhenAll(t1.AsTask(), t2.AsTask(), t3.AsTask());

		process.Status.Should().Be(TransportStatus.Disposed);
		process.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task DisposeAsync_WhenCalledMultipleTimes_ShouldBeIdempotent()
	{
		Log("Starting test: DisposeAsync_WhenCalledMultipleTimes_ShouldBeIdempotent");
		var process = Transport().Build();
		await process.StartAsync();

		await process.DisposeAsync();
		await process.DisposeAsync();

		process.IsStarted.Should().BeFalse();
		process.Status.Should().Be(TransportStatus.Disposed);
	}

	[Fact]
	public async Task DisposeAsync_WhenDisposed_ShouldSetIsHealthyToFalse()
	{
		Log("Starting test: DisposeAsync_WhenDisposed_ShouldSetIsHealthyToFalse");
		var process = Transport().Build();
		await process.StartAsync();

		process.IsHealthy.Should().BeTrue();

		await process.DisposeAsync();

		process.IsHealthy.Should().BeFalse();
	}

	[Fact]
	public async Task DisposeAsync_WhenDisposedAfterStart_ShouldStopProcessAndResetIsStarted()
	{
		Log("Starting test: DisposeAsync_WhenDisposedAfterStart_ShouldStopProcessAndResetIsStarted");
		var process = Transport().Build();

		await process.StartAsync();
		await process.DisposeAsync();

		process.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task DisposeAsync_WhenNormalDisposal_ShouldNotRaiseErrorEvent()
	{
		Log("Starting test: DisposeAsync_WhenNormalDisposal_ErrorEventShouldNotBeRaised");
		var process = Transport().Build();
		await process.StartAsync();

		var errorRaised = false;
		process.Error += _ => errorRaised = true;

		await process.DisposeAsync();

		errorRaised.Should().BeFalse("normal disposal should not raise error event");
	}

	[Fact]
	public async Task DisposeAsync_WhenProcessExitWaitFaults_ShouldSetFailedStatusAndThrow()
	{
		Log("Starting test: DisposeAsync_WhenProcessExitWaitFaults_ShouldSetFailedStatusAndThrow");
		var teardownFailure = new InvalidOperationException("Injected process-exit wait failure.");
		var errors          = new List<Exception>();

		var transport = CreateExitWaitFailureTransport(teardownFailure, out var injectedFailures);

		transport.Error += ex => errors.Add(ex);
		await transport.StartAsync();

		var act = async () => await transport.DisposeAsync();

		await act.Should().ThrowAsync<InvalidOperationException>()
				 .WithMessage(teardownFailure.Message);

		injectedFailures().Should().Be(1, "the fault must come from the teardown wait, not an exited child or the background monitor");
		transport.Status.Should().Be(TransportStatus.Failed);
		transport.IsStarted.Should().BeFalse();
		errors.Should().Contain(ex => ex.Message == teardownFailure.Message);
	}

	[Fact]
	public async Task DisposeAsync_WhenSendQuitOnDisposeFalse_ShouldNotSendQuitCommand()
	{
		Log("Starting test: DisposeAsync_WhenSendQuitOnDisposeFalse_ShouldNotSendQuitCommand");
		var quitSent = false;

		await using var transport = Transport()
									.WithSendQuitOnDispose(false)
									.WithQuitGracePeriod(TestConstants.QuitGracePeriod)
									.WithTeardownTimeout(TestConstants.DefaultTimeout)
									.WithOnQuitSent(() => quitSent = true)
									.Build();

		await transport.StartAsync();
		await transport.DisposeAsync();

		quitSent.Should().BeFalse("quit command should not be sent when SendQuitOnDispose is false");
		transport.Status.Should().Be(TransportStatus.Disposed);
		transport.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task DisposeAsync_WhenSendQuitOnDisposeTrue_ShouldSendQuitCommand()
	{
		Log("Starting test: DisposeAsync_WhenSendQuitOnDisposeTrue_ShouldSendQuitCommand");
		var quitSent = false;

		await using var transport = Transport()
									.WithSendQuitOnDispose()
									.WithQuitGracePeriod(TestConstants.QuitGracePeriod)
									.WithTeardownTimeout(TestConstants.DefaultTimeout)
									.WithOnQuitSent(() => quitSent = true)
									.Build();

		await transport.StartAsync();
		await transport.DisposeAsync();

		quitSent.Should().BeTrue("quit command should be sent when SendQuitOnDispose is true");
		transport.Status.Should().Be(TransportStatus.Disposed);
	}

	[Fact]
	public async Task DisposeAsync_WhileStarting_WhenCalled_ShouldWaitForStartThenDispose()
	{
		Log("Starting test: DisposeAsync_WhileStarting_ShouldWaitForStartThenDispose");
		var process = Transport().Build();

		var startTask   = process.StartAsync();
		var disposeTask = process.DisposeAsync().AsTask();

		await Task.WhenAll(startTask, disposeTask);

		process.Status.Should().Be(TransportStatus.Disposed);
		process.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task DisposeAsync_WhileStopping_WhenCalled_ShouldWaitForStopThenComplete()
	{
		Log("Starting test: DisposeAsync_WhileStopping_ShouldWaitForStopThenComplete");
		var process = Transport().Build();
		await process.StartAsync();

		var stopTask    = process.StopAsync();
		var disposeTask = process.DisposeAsync().AsTask();

		await Task.WhenAll(stopTask, disposeTask);

		process.Status.Should().Be(TransportStatus.Disposed);
	}

	[Fact]
	public async Task DisposeAsync_WithActiveReader_WhenCalled_ShouldCompleteReaderGracefully()
	{
		Log("Starting test: DisposeAsync_WithActiveReader_ShouldCompleteReaderGracefully");
		var process = Transport().Build();
		await process.StartAsync();

		var enumerator = process.ReadLinesAsync(CancellationToken.None).GetAsyncEnumerator();
		var readTask   = enumerator.MoveNextAsync().AsTask();

		await process.DisposeAsync();
		await AsyncTestHelpers.WaitForAsyncEnumeratorToCompleteAsync(enumerator, readTask);

		await enumerator.DisposeAsync();
	}

	[Fact]
	public async Task DisposeAsync_WithActiveWriter_WhenCalled_ShouldCloseChannelAndThrowOnWrite()
	{
		Log("Starting test: DisposeAsync_WithActiveWriter_ShouldCloseChannelAndThrowOnWrite");
		var process = Transport().Build();
		await process.StartAsync();

		var writeTask = process.WriteLineAsync("uci");

		await process.DisposeAsync();

		await FluentActions.Awaiting(() => process.WriteLineAsync("isready"))
						   .Should()
						   .ThrowAsync<InvalidOperationException>();

		try
		{
			await writeTask;
		}
		catch { }
	}

	[Fact]
	public async Task DisposeAsync_WithExitedProcess_WhenCalled_ShouldCompleteImmediately()
	{
		Log("Starting test: DisposeAsync_WithExitedProcess_ShouldCompleteImmediately");

		var transport = ExitingProcess()
						.Build();

		var exitedTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		transport.Exited += (_, _) => exitedTcs.TrySetResult(null);

		await transport.StartAsync();

		var completed = await Task.WhenAny(exitedTcs.Task, Task.Delay(TestConstants.DefaultTimeout));
		completed.Should().Be(exitedTcs.Task);

		using var cts         = new CancellationTokenSource(TestConstants.DefaultTimeout);
		var       disposeTask = transport.DisposeAsync().AsTask();

		var disposeCompleted = await Task.WhenAny(
								   disposeTask,
								   Task.Delay(TestConstants.DefaultTimeout, cts.Token)
							   );

		disposeCompleted.Should().Be(disposeTask, "Dispose should complete when process has already exited");

		await disposeTask;
		transport.Status.Should().Be(TransportStatus.Disposed);
	}

	[Fact]
	public async Task DisposeAsync_WithShortTeardownTimeout_WhenCalled_ShouldCompleteWithinTimeout()
	{
		Log("Starting test: DisposeAsync_WithShortTeardownTimeout_ShouldCompleteWithinTimeout");
		await using var transport = Transport()
									.WithTeardownTimeout(TestConstants.ShortTimeout)
									.WithSendQuitOnDispose()
									.Build();

		await transport.StartAsync();

		var disposeStart = DateTime.UtcNow;
		await transport.DisposeAsync();
		var disposeDuration = DateTime.UtcNow - disposeStart;

		disposeDuration.Should().BeLessThan(
			TestConstants.DefaultTimeout,
			"Dispose should complete within reasonable time"
		);

		transport.Status.Should().Be(TransportStatus.Disposed);
	}

	[Fact]
	public async Task StartAsync_WhenAlreadyStarted_ShouldBeIdempotent()
	{
		Log("Starting test: StartAsync_WhenAlreadyStarted_ShouldBeIdempotent");
		await using var process = Transport().Build();
		await process.StartAsync();

		var before = process.Status;

		await process.StartAsync();
		var after = process.Status;

		process.IsStarted.Should().BeTrue();
		before.Should().Be(TransportStatus.Started);
		after.Should().Be(TransportStatus.Started);
	}

	[Fact]
	public async Task StartAsync_WhenCalledWithValidProcess_ShouldStartProcess()
	{
		Log("Starting test: StartAsync_WhenCalledWithValidProcess_ShouldStartProcess");
		await using var process = Transport().Build();

		await process.StartAsync();

		process.IsStarted.Should().BeTrue();
	}

	[Fact]
	public async Task StartAsync_WhenCanceled_ShouldThrowAndCleanState()
	{
		Log("Starting test: StartAsync_WhenCanceled_ShouldThrowAndCleanState");
		var process = Transport().WithPath("any/nonempty/path").Build();
		var cts     = new CancellationTokenSource();
		cts.Cancel();

		await FluentActions.Awaiting(() => process.StartAsync(cts.Token))
						   .Should()
						   .ThrowAsync<OperationCanceledException>();

		process.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task StartAsync_WhenConcurrentCalls_ShouldStartOnceAndCompleteAll()
	{
		Log("Starting test: StartAsync_WhenConcurrentCalls_ShouldStartOnceAndCompleteAll");
		await using var process = Transport().Build();

		var t1 = process.StartAsync();
		var t2 = process.StartAsync();

		await Task.WhenAll(t1, t2);

		process.IsStarted.Should().BeTrue();
	}

	[Fact]
	public async Task StartAsync_WhenConcurrentCallsAndOneFails_ShouldHandleGracefully()
	{
		Log("Starting test: StartAsync_WhenConcurrentCallsAndOneFails_ShouldHandleGracefully");
		string missing = Path.Combine(
			Path.GetTempPath(),
			"no-such-dir",
			Guid.NewGuid().ToString("N"),
			"missing.exe"
		);

		await using var transport = Transport().WithPath(missing).Build();

		var start1 = transport.StartAsync();
		var start2 = transport.StartAsync();
		var start3 = transport.StartAsync();

		string[] allTasks = await Task.WhenAll(
								start1.ContinueWith(t => t.Exception != null ? "failed" : "success"),
								start2.ContinueWith(t => t.Exception != null ? "failed" : "success"),
								start3.ContinueWith(t => t.Exception != null ? "failed" : "success")
							);

		allTasks.Should().AllBe("failed", "All concurrent StartAsync calls should fail when path is invalid");
		transport.IsStarted.Should().BeFalse();
		transport.Status.Should().Be(TransportStatus.Failed);
	}

	[Fact]
	public async Task StartAsync_WhenConcurrentWithStopAsync_ShouldHandleGracefully()
	{
		Log("Starting test: StartAsync_WhenConcurrentWithStopAsync_ShouldHandleGracefully");
		await using var transport = Transport().Build();

		var startTask = transport.StartAsync();
		await Task.Delay(TestConstants.ShortDelay);

		var stopTask = transport.StopAsync();

		await Task.WhenAll(startTask, stopTask);

		transport.Status.Should().Be(TransportStatus.Stopped);
		transport.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task StartAsync_WhenDisposed_ShouldThrowObjectDisposedException()
	{
		Log("Starting test: StartAsync_WhenDisposed_ShouldThrowObjectDisposedException");
		var process = Transport().WithPath("any/nonempty/path").Build();
		await process.DisposeAsync();

		await FluentActions.Awaiting(() => process.StartAsync())
						   .Should()
						   .ThrowAsync<ObjectDisposedException>();
	}

	[Fact]
	public async Task StartAsync_WhenFailsEarly_ShouldSetFailedStatusAndNotRaiseErrorEvent()
	{
		Log("Starting test: StartAsync_WhenFailsEarly_ShouldSetFailedStatusAndNotRaiseErrorEvent");
		string missing = Path.Combine(
			Path.GetTempPath(),
			"no-such-dir",
			Guid.NewGuid().ToString("N"),
			"missing.exe"
		);

		await using var transport = Transport().WithPath(missing).Build();

		var errors = new List<Exception>();
		transport.Error += ex => errors.Add(ex);

		try
		{
			await transport.StartAsync();
		}
		catch (ArgumentException)
		{
			// Expected to throw
		}

		await Task.Delay(TestConstants.StandardDelay);

		errors.Should().BeEmpty(
			"Error events should not be raised when start fails early (before initialization)"
		);

		transport.Status.Should().Be(TransportStatus.Failed, "Status should be set to Failed when start fails");
		transport.IsStarted.Should().BeFalse("Transport should not be started after failure");
	}

	[Fact]
	public async Task StartAsync_WhenMultipleConcurrent_ShouldStartOnceAndAwaitOthers()
	{
		Log("Starting test: StartAsync_WhenMultipleConcurrent_ShouldStartOnceAndAwaitOthers");
		await using var process = Transport().Build();

		var t1 = process.StartAsync();
		var t2 = process.StartAsync();
		var t3 = process.StartAsync();

		await Task.WhenAll(t1, t2, t3);

		process.IsStarted.Should().BeTrue();
		process.Status.Should().Be(TransportStatus.Started);
	}

	[Fact]
	public async Task StartAsync_WhenStopping_ShouldWaitThenRestart()
	{
		var enteredStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseStop = new ManualResetEventSlim();
		await using var process = Transport().WithOnQuitSent(() =>
		{
			enteredStop.TrySetResult();
			releaseStop.Wait(TestConstants.DefaultTimeout);
		}).Build();
		await process.StartAsync();

		var stoppingTask = Task.Run(() => process.StopAsync());
		try
		{
			await enteredStop.Task.WaitAsync(TestConstants.DefaultTimeout);
			process.Status.Should().Be(TransportStatus.Stopping);
			var restartingTask = process.StartAsync();
			restartingTask.IsCompleted.Should().BeFalse("startup must wait for the previous process teardown");
			releaseStop.Set();
			await Task.WhenAll(stoppingTask, restartingTask).WaitAsync(TestConstants.DefaultTimeout);
			process.Status.Should().Be(TransportStatus.Started);
			process.IsHealthy.Should().BeTrue();
		}
		finally
		{
			releaseStop.Set();
		}
	}

	[Fact]
	public async Task StartAsync_WithInvalidWorkingDirectory_WhenCalled_ShouldThrowArgumentException()
	{
		Log("Starting test: StartAsync_WithInvalidWorkingDirectory_ShouldThrowArgumentException");
		string invalidDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

		await using var process = Transport()
								  .WithWorkingDirectory(invalidDir)
								  .Build();

		await FluentActions.Awaiting(() => process.StartAsync())
						   .Should()
						   .ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task StartAsync_WithMissingPath_WhenCalled_ShouldThrowAndCleanup()
	{
		Log("Starting test: StartAsync_WithMissingPath_ShouldThrowAndCleanup");
		string missing = Path.Combine(
			Path.GetTempPath(),
			"no-such-dir",
			Guid.NewGuid().ToString("N"),
			"missing.exe"
		);

		await using var transport = Transport().WithPath(missing).Build();

		await FluentActions.Awaiting(() => transport.StartAsync())
						   .Should()
						   .ThrowAsync<ArgumentException>();

		transport.IsStarted.Should().BeFalse();
		transport.Status.Should().Be(TransportStatus.Failed);
	}

	[Fact]
	public async Task StopAsync_WhenCalled_ShouldDisposeStreamsAndCompleteChannels()
	{
		Log("Starting test: StopAsync_WhenCalled_ShouldDisposeStreamsAndCompleteChannels");
		await using var process = Transport().Build();
		await process.StartAsync();

		var enumerator = process.ReadLinesAsync(CancellationToken.None).GetAsyncEnumerator();
		var pending    = enumerator.MoveNextAsync().AsTask();

		await process.StopAsync();
		await AsyncTestHelpers.WaitForAsyncEnumeratorToCompleteAsync(enumerator, pending);

		await enumerator.DisposeAsync();
	}

	[Fact]
	public async Task StopAsync_WhenCanceled_ShouldThrowOperationCanceledExceptionAndLeaveStateUnchanged()
	{
		Log("Starting test: StopAsync_WhenCanceled_ShouldThrowOperationCanceledExceptionAndLeaveStateUnchanged");
		var process = Transport().WithPath("any/nonempty/path").Build();
		var cts     = new CancellationTokenSource();
		cts.Cancel();

		await FluentActions.Awaiting(() => process.StopAsync(cts.Token))
						   .Should()
						   .ThrowAsync<OperationCanceledException>();

		process.Status.Should().Be(TransportStatus.Created);
	}

	[Fact]
	public async Task StopAsync_WhenConcurrentCalls_ShouldCompleteAll()
	{
		Log("Starting test: StopAsync_WhenConcurrentCalls_ShouldCompleteAll");
		await using var transport = Transport().Build();
		await transport.StartAsync();

		var stop1 = transport.StopAsync();
		var stop2 = transport.StopAsync();
		var stop3 = transport.StopAsync();

		await Task.WhenAll(stop1, stop2, stop3);

		transport.Status.Should().Be(TransportStatus.Stopped);
		transport.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task StopAsync_WhenDisposed_ShouldThrowObjectDisposedException()
	{
		Log("Starting test: StopAsync_WhenDisposed_ShouldThrowObjectDisposedException");
		var process = Transport().WithPath("any/nonempty/path").Build();
		await process.DisposeAsync();

		await FluentActions.Awaiting(() => process.StopAsync())
						   .Should()
						   .ThrowAsync<ObjectDisposedException>();
	}

	[Fact]
	public async Task StopAsync_WhenProcessExitWaitFaults_ShouldSetFailedStatusAndThrow()
	{
		Log("Starting test: StopAsync_WhenProcessExitWaitFaults_ShouldSetFailedStatusAndThrow");
		var teardownFailure = new InvalidOperationException("Injected process-exit wait failure.");
		var errors          = new List<Exception>();

		var transport = CreateExitWaitFailureTransport(teardownFailure, out var injectedFailures);

		transport.Error += ex => errors.Add(ex);
		await transport.StartAsync();

		var act = async () => await transport.StopAsync();

		await act.Should().ThrowAsync<InvalidOperationException>()
				 .WithMessage(teardownFailure.Message);

		injectedFailures().Should().Be(1, "the fault must come from the teardown wait, not an exited child or the background monitor");
		transport.Status.Should().Be(TransportStatus.Failed);
		transport.IsStarted.Should().BeFalse();
		errors.Should().Contain(ex => ex.Message == teardownFailure.Message);
	}

	[Fact]
	public async Task StopAsync_WhenSendQuitOnStop_ShouldSendQuitCommandAndThenKillAfterGrace()
	{
		Log("Starting test: StopAsync_WhenSendQuitOnStop_ShouldSendQuitCommandAndThenKillAfterGrace");
		var quitSent = false;

		await using var transport = Transport()
									.WithSendQuitOnStop()
									.WithQuitGracePeriod(TestConstants.QuitGracePeriod)
									.WithOnQuitSent(() => quitSent = true)
									.Build();

		await transport.StartAsync();

		await transport.StopAsync();

		quitSent.Should().BeTrue("quit command should be sent when SendQuitOnStop is true");
		transport.Status.Should().Be(TransportStatus.Stopped);
	}

	private static ProcessUciTransport CreateExitWaitFailureTransport(Exception failure, out Func<int> injectedFailures)
	{
		var failureCount = 0;
		injectedFailures = () => Volatile.Read(ref failureCount);
		return RunningProcess().WithWaitForProcessExitOverride((process, token) =>
		{
			// The background exit monitor uses CancellationToken.None. Inject once in the
			// bounded teardown wait, then allow the forced-kill wait to confirm actual exit.
			if (token.CanBeCanceled && Interlocked.CompareExchange(ref failureCount, 1, 0) == 0)
				return Task.FromException(failure);
			return process.WaitForExitAsync(token);
		}).Build();
	}
}
