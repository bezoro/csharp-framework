using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Internal;

public sealed class SerializedUciEngineClientRuntimeConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingDoesNotInvalidateAnInflightFinallyAndRejectsQueuedCallbacks(bool useWrapper)
    {
        var (transport, output) = UciEngineClientTestHelpers.CreateMockTransport();
        transport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => output.Writer.TryWrite("readyok"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.WriteLineAsync("debug on", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        var client = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, output);
        var runtime = new SerializedUciEngineClientRuntime(client);
        Task Debug(bool enabled) => useWrapper ? runtime.SetDebugAsync(enabled) : client.SetDebugAsync(enabled, default);
        ValueTask Dispose() => useWrapper ? runtime.DisposeAsync() : client.DisposeAsync();
        try
        {
            var active = Debug(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = Debug(false);
            Assert.False(queued.IsCompleted);

            await Task.WhenAll(Dispose().AsTask(), Dispose().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();

            await active.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            await transport.DidNotReceive().WriteLineAsync("debug off", Arg.Any<CancellationToken>());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Debug(false));
        }
        finally
        {
            release.TrySetResult();
            await Dispose();
        }
    }
}
