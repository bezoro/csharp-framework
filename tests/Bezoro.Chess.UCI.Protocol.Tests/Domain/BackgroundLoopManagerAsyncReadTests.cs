using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public class BackgroundLoopManagerAsyncReadTests
{
	[Theory]
	[InlineData("utf-8")]
	[InlineData("utf-16")]
	public async Task SplitPreambleAndMultibyteText_ShouldUseConfiguredEncoding(string encodingName)
	{
		var encoding = Encoding.GetEncoding(encodingName);
		using var pipe = new AsyncOnlyStream();
		using var reader = new StreamReader(pipe, encoding);
		var lines = new BoundedLineReader(reader, 4);
		foreach (byte value in encoding.GetPreamble().Concat(encoding.GetBytes("é😀\n")))
			pipe.Feed([value]);
		(await lines.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("é😀");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OverlongUnterminatedLine_ShouldFaultBeforeEof(bool useStderr)
	{
		using var pipe = new AsyncOnlyStream();
		using var reader = new StreamReader(pipe);
		var output = Channel.CreateUnbounded<string>();
		var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = new BackgroundLoopManager(new() { MaxIncomingLineLength = 8 },
			(ex, _) => failure.TrySetResult(ex), new());
		manager.InitializeCancellationSources();
		try
		{
			if (useStderr) manager.StartStderrLoopIfNeeded(reader);
			else manager.StartReadLoop(reader, output.Writer);
			pipe.Feed("12345");
			pipe.Feed("6789");
			(await failure.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeOfType<InvalidDataException>();
			if (!useStderr)
				await FluentActions.Awaiting(() => output.Reader.Completion).Should().ThrowAsync<InvalidDataException>();
		}
		finally
		{
			pipe.Complete();
			await Task.WhenAll(manager.AwaitReadLoopAsync(), manager.AwaitStderrLoopAsync());
			manager.DisposeCancellationSources();
		}
	}

	[Fact]
	public async Task WriteFailure_ShouldCloseOutgoingChannelAndWakeBlockedWriters()
	{
		using var sink = new MemoryStream();
		var stdin = new StreamWriter(sink);
		sink.Close();
		var output = Channel.CreateBounded<string>(1);
		output.Writer.TryWrite("uci");
		var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = new BackgroundLoopManager(new(), (ex, _) => failed.TrySetResult(ex), new());
		manager.InitializeCancellationSources();
		manager.StartWriteLoop(output.Reader, stdin, output.Writer);
		await manager.AwaitWriteLoopAsync().WaitAsync(TimeSpan.FromSeconds(5));
		(await failed.Task).Should().BeOfType<ObjectDisposedException>();
		output.Writer.TryWrite("isready").Should().BeFalse();
		await FluentActions.Awaiting(() => output.Writer.WriteAsync("isready").AsTask())
			.Should().ThrowAsync<ChannelClosedException>();
		manager.DisposeCancellationSources();
	}

	[Fact]
	public async Task ReadLoops_ShouldUseAsyncReadsAndForwardUtf8Lines()
	{
		using var stdout = new AsyncOnlyStream();
		using var stderr = new AsyncOnlyStream();
		using var stdoutReader = new StreamReader(stdout, Encoding.UTF8);
		using var stderrReader = new StreamReader(stderr, Encoding.UTF8);
		var output = Channel.CreateUnbounded<string>();
		var errors = new ConcurrentQueue<Exception>();
		var stderrLines = new ConcurrentQueue<string>();
		var manager = new BackgroundLoopManager(new(), (error, _) => errors.Enqueue(error), new(), stderrLines.Enqueue);
		manager.InitializeCancellationSources();
		manager.StartReadLoop(stdoutReader, output.Writer);
		manager.StartStderrLoopIfNeeded(stderrReader);
		try
		{
			string fullChunk = new('a', 1023);
			stdout.Feed(fullChunk + "\n");
			(await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(fullChunk);
			stdout.Feed("first\n\nOlá 世界\n");
			stderr.Feed("stderr\n\nOlá 世界\n");
			stdout.Complete();
			stderr.Complete();
			await Task.WhenAll(manager.AwaitReadLoopAsync(), manager.AwaitStderrLoopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
			var lines = new List<string>();
			await foreach (string line in output.Reader.ReadAllAsync()) lines.Add(line);
			lines.Should().Equal("first", "Olá 世界");
			stderrLines.Should().Equal("stderr", "Olá 世界");
			stdout.SynchronousReads.Should().Be(0);
			stderr.SynchronousReads.Should().Be(0);
			errors.Should().BeEmpty();
		}
		finally
		{
			stdout.Complete();
			stderr.Complete();
			await manager.AwaitReadLoopAsync();
			await manager.AwaitStderrLoopAsync();
			manager.DisposeCancellationSources();
		}
	}

	[Fact]
	public async Task IdleReaders_ShouldYieldWhileWaitingAndJoinAfterDisposal()
	{
		using var stdout = new AsyncOnlyStream();
		using var stderr = new AsyncOnlyStream();
		var stdoutReader = new StreamReader(stdout, Encoding.UTF8);
		var stderrReader = new StreamReader(stderr, Encoding.UTF8);
		var errors = new ConcurrentQueue<Exception>();
		var manager = new BackgroundLoopManager(new(), (error, _) => errors.Enqueue(error), new());
		manager.InitializeCancellationSources();
		manager.StartReadLoop(stdoutReader, Channel.CreateUnbounded<string>().Writer);
		manager.StartStderrLoopIfNeeded(stderrReader);
		try
		{
			await Task.WhenAll(stdout.ReadStarted.Task, stderr.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
			stdout.PendingReads.Should().Be(1);
			stderr.PendingReads.Should().Be(1);
			// The streams reject synchronous reads and keep ReadAsync incomplete until disposal.
			// Other pool work must run while both engine readers remain idle.
			(await Task.Run(() => 42).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
			stdout.SynchronousReads.Should().Be(0);
			stderr.SynchronousReads.Should().Be(0);
		}
		finally
		{
			stdoutReader.Dispose();
			stderrReader.Dispose();
			await Task.WhenAll(manager.AwaitReadLoopAsync(), manager.AwaitStderrLoopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
			manager.DisposeCancellationSources();
		}
		errors.Should().BeEmpty();
		stdout.PendingReads.Should().Be(0);
		stderr.PendingReads.Should().Be(0);
	}

	private sealed class AsyncOnlyStream : Stream
	{
		private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
		private int _synchronousReads;
		private int _pendingReads;
		public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int SynchronousReads => Volatile.Read(ref _synchronousReads);
		public int PendingReads => Volatile.Read(ref _pendingReads);
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));
		public void Feed(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
		public void Complete() => _chunks.Writer.TryComplete();

		public override int Read(byte[] buffer, int offset, int count)
		{
			Interlocked.Increment(ref _synchronousReads);
			throw new InvalidOperationException("Synchronous reads are forbidden for an async engine pipe.");
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _pendingReads);
			ReadStarted.TrySetResult();
			try
			{
				if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
				if (!_chunks.Reader.TryRead(out byte[]? chunk)) throw new InvalidOperationException("Only one reader is allowed.");
				chunk.AsMemory().CopyTo(buffer);
				return chunk.Length;
			}
			finally { Interlocked.Decrement(ref _pendingReads); }
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing) Complete();
			base.Dispose(disposing);
		}

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
