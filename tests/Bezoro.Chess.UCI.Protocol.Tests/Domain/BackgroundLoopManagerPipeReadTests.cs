using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;
using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public class BackgroundLoopManagerPipeReadTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task FullPipeChunkEndingInNewline_ShouldDeliverBeforeAnotherWrite()
	{
		using var pipe = new TestPipe(false);
		var channel = Channel.CreateBounded<string>(1);
		var manager = new BackgroundLoopManager(new(), (_, _) => { }, new());
		manager.InitializeCancellationSources();
		manager.StartReadLoop(pipe.Reader, channel.Writer);
		try
		{
			string line = new('x', 1023);
			pipe.Feed(line + "\n");
			(await channel.Reader.ReadAsync().AsTask().WaitAsync(Timeout)).Should().Be(line);
		}
		finally
		{
			pipe.Complete();
			await manager.AwaitReadLoopAsync();
			manager.DisposeCancellationSources();
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IdleSynchronousPipes_ShouldLeavePoolWorkersForCommandsAndPreserveUtf8Lines(bool useFileStream)
	{
		using var stdout = new TestPipe(useFileStream);
		using var stderr = new TestPipe(useFileStream);
		using var commands = new MemoryStream();
		using var stdin = new StreamWriter(commands, new UTF8Encoding(false), leaveOpen: true);
		var outgoing = Channel.CreateUnbounded<string>();
		var output = Channel.CreateUnbounded<string>();
		var received = new ConcurrentQueue<(string Line, bool OnPool)>();
		var errors = new ConcurrentQueue<Exception>();
		var metrics = new BackgroundLoopMetrics();
		var manager = new BackgroundLoopManager(new(), (error, _) => errors.Enqueue(error), metrics,
			line => received.Enqueue((line, Thread.CurrentThread.IsThreadPoolThread)));
		manager.InitializeCancellationSources();
		manager.StartReadLoop(stdout.Reader, output.Writer);
		manager.StartStderrLoopIfNeeded(stderr.Reader);
		manager.StartWriteLoop(outgoing.Reader, stdin);
		try
		{
			await Task.WhenAll(stdout.Probe.ReadStarted.Task, stderr.Probe.ReadStarted.Task).WaitAsync(Timeout);
			// A legacy ReadLineAsync may synchronously block before returning its Task.
			// Both idle pipes must therefore belong to background threads outside the shared pool.
			stdout.Probe.Reads.Should().OnlyContain(read => !read.OnPool && read.IsBackground);
			stderr.Probe.Reads.Should().OnlyContain(read => !read.OnPool && read.IsBackground);
			stdout.Probe.ReadCompleted.Task.IsCompleted.Should().BeFalse();
			stderr.Probe.ReadCompleted.Task.IsCompleted.Should().BeFalse();

			outgoing.Writer.TryWrite("uci").Should().BeTrue();
			outgoing.Writer.TryComplete().Should().BeTrue();
			await manager.AwaitWriteLoopAsync().WaitAsync(Timeout);
			Encoding.UTF8.GetString(commands.ToArray()).Should().Be("uci" + Environment.NewLine);
			metrics.LinesWritten.Should().Be(1);
			stdout.Probe.ReadCompleted.Task.IsCompleted.Should().BeFalse("command writes must not wait for engine output");

			stdout.Feed("first\n\nOlá 世界\n");
			stderr.Feed("warning\n\nsecond\n");
		}
		finally
		{
			outgoing.Writer.TryComplete();
			stdout.Complete();
			stderr.Complete();
			await Task.WhenAll(manager.AwaitReadLoopAsync(), manager.AwaitStderrLoopAsync(), manager.AwaitWriteLoopAsync())
				.WaitAsync(Timeout);
			manager.DisposeCancellationSources();
		}

		var lines = new List<string>();
		await foreach (string line in output.Reader.ReadAllAsync()) lines.Add(line);
		lines.Should().Equal("first", "Olá 世界");
		metrics.LinesRead.Should().Be(2);
		received.Select(item => item.Line).Should().Equal("warning", "second");
		received.Should().OnlyContain(item => item.OnPool, "stderr handlers retain their public callback context");
		stdout.Probe.Reads.Should().OnlyContain(read => !read.OnPool && read.IsBackground);
		stderr.Probe.Reads.Should().OnlyContain(read => !read.OnPool && read.IsBackground);
		errors.Should().BeEmpty();
	}

	[Fact]
	public async Task SynchronousStdout_WhenItsBoundedChannelIsFull_ShouldJoinOnCancellationWithoutWaitingForEof()
	{
		using var stdout = new TestPipe(false);
		var output = Channel.CreateBounded<string>(1);
		var metrics = new BackgroundLoopMetrics();
		var errors = new ConcurrentQueue<Exception>();
		var manager = new BackgroundLoopManager(new(), (error, _) => errors.Enqueue(error), metrics);
		manager.InitializeCancellationSources();
		manager.StartReadLoop(stdout.Reader, output.Writer);
		try
		{
			await stdout.Probe.ReadStarted.Task.WaitAsync(Timeout);
			stdout.Feed("first\nsecond\n");
			(await AsyncTestHelpers.WaitForConditionAsync(() => metrics.BackpressureEvents > 0, timeout: Timeout))
				.Should().BeTrue("the second line must encounter the bounded output channel");

			manager.CancelReadLoop();
			await manager.AwaitReadLoopAsync().WaitAsync(Timeout);
			stdout.IsCompleted.Should().BeFalse("cancelling channel backpressure must not require child-process EOF");
			output.Reader.TryRead(out string? line).Should().BeTrue();
			line.Should().Be("first");
			await output.Reader.Completion.WaitAsync(Timeout);
			errors.Should().BeEmpty();
		}
		finally
		{
			stdout.Complete();
			manager.CancelReadLoop();
			await manager.AwaitReadLoopAsync().WaitAsync(Timeout);
			manager.DisposeCancellationSources();
		}
	}

	[Fact]
	public async Task SynchronousReaderFailure_ShouldReportOnPoolAndFaultTheOutputChannel()
	{
		var failure = new IOException("controlled pipe read failure");
		using var stdout = new TestPipe(false, failure);
		var output = Channel.CreateUnbounded<string>();
		var errors = new ConcurrentQueue<(Exception Error, string Context, bool OnPool)>();
		var manager = new BackgroundLoopManager(new(),
			(error, context) => errors.Enqueue((error, context, Thread.CurrentThread.IsThreadPoolThread)), new());
		manager.InitializeCancellationSources();
		manager.StartReadLoop(stdout.Reader, output.Writer);
		try
		{
			await manager.AwaitReadLoopAsync().WaitAsync(Timeout);
			errors.Should().ContainSingle();
			var report = errors.Single();
			report.Error.Should().BeSameAs(failure);
			report.Context.Should().Be("Read loop faulted.");
			report.OnPool.Should().BeTrue();
			await FluentActions.Awaiting(() => output.Reader.Completion).Should().ThrowAsync<IOException>();
		}
		finally
		{
			stdout.Complete();
			await manager.AwaitReadLoopAsync().WaitAsync(Timeout);
			manager.DisposeCancellationSources();
		}
	}

	private sealed class TestPipe : IDisposable
	{
		private readonly AnonymousPipeServerStream _writerPipe = new(PipeDirection.Out, HandleInheritability.None);
		private readonly AnonymousPipeClientStream _readerPipe;
		private readonly StreamWriter _writer;
		public StreamReader Reader { get; }
		public ReadProbe Probe { get; }
		public bool IsCompleted { get; private set; }

		public TestPipe(bool useFileStream, IOException? failure = null)
		{
			var readHandle = new SafePipeHandle(_writerPipe.ClientSafePipeHandle.DangerousGetHandle(), ownsHandle: true);
			_readerPipe = new(PipeDirection.In, readHandle);
			// Transfer the local read handle so closing the writer only sends EOF.
			_writerPipe.ClientSafePipeHandle.SetHandleAsInvalid();
			// Mono exposes redirected process pipes as synchronous FileStreams.
			// Keep the actual pipe owner alive while the FileStream borrows its handle.
			Probe = new ReadProbe(failure);
			IntPtr handle = _readerPipe.SafePipeHandle.DangerousGetHandle();
			Stream input = useFileStream
				? new RecordingFileStream(new SafeFileHandle(handle, ownsHandle: false), Probe)
				: new RecordingPipeStream(_readerPipe, Probe);
			Reader = new(input, new UTF8Encoding(false, true));
			_writer = new(_writerPipe, new UTF8Encoding(false)) { AutoFlush = true };
		}

		public void Feed(string text) => _writer.Write(text);
		public void Complete()
		{
			if (IsCompleted) return;
			IsCompleted = true;
			_writer.Dispose();
		}
		public void Dispose()
		{
			Complete();
			Reader.Dispose();
			_readerPipe.Dispose();
			_writerPipe.Dispose();
		}
	}

	private sealed class RecordingFileStream(SafeFileHandle handle, ReadProbe probe)
		: FileStream(handle, FileAccess.Read, 4096, isAsync: false)
	{
		public override int Read(byte[] buffer, int offset, int count)
		{
			probe.Begin();
			int read = base.Read(buffer, offset, count);
			probe.ReadCompleted.TrySetResult();
			return read;
		}
	}

	private sealed class RecordingPipeStream(AnonymousPipeClientStream pipe, ReadProbe probe)
		: PipeStream(PipeDirection.In, 4096)
	{
		public override int Read(byte[] buffer, int offset, int count)
		{
			probe.Begin();
			int read = pipe.Read(buffer, offset, count);
			probe.ReadCompleted.TrySetResult();
			return read;
		}
	}

	private sealed class ReadProbe(IOException? failure)
	{
		public ConcurrentQueue<(bool OnPool, bool IsBackground)> Reads { get; } = new();
		public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Begin()
		{
			Reads.Enqueue((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.IsBackground));
			ReadStarted.TrySetResult();
			if (failure is not null) throw failure;
		}
	}
}
