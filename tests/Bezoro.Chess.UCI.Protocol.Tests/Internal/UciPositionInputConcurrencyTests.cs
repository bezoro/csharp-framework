using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Internal;

public sealed class UciPositionInputConcurrencyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PositionInputCanJoinAnotherGatedOperationBeforeCommandAdmission(int operation)
    {
        var (transport, output) = UciEngineClientTestHelpers.CreateMockTransport();
        transport.IsStarted.Returns(true);
        transport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => output.Writer.TryWrite("readyok"));
        transport.When(t => t.WriteLineAsync(Arg.Is<string>(s => s.StartsWith("go ")), Arg.Any<CancellationToken>()))
            .Do(_ => output.Writer.TryWrite("bestmove e2e4"));
        await using var client = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, output);
        await using var runtime = new SerializedUciEngineClientRuntime(client);
        Task? reader = null;
        int enumerated = 0;
        IEnumerable<string> Moves()
        {
            enumerated++;
            reader = Task.Factory.StartNew(() => operation == 0 ? client.IsReadyAsync(default) : runtime.SetDebugAsync(false),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            if (!reader.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Caller position enumeration ran inside a command gate.");
            yield return "e2e4";
        }
        try
        {
            if (operation == 0) await client.SetPositionAsync(Fen.Default, Moves(), default);
            else if (operation == 1) await runtime.SetPositionAsync(Fen.Default, Moves());
            else await runtime.SearchAsync(Fen.Default, Moves(), new() { Depth = 1 });

            Assert.Equal(1, enumerated);
            await transport.Received(1).WriteLineAsync(
                Arg.Is<string>(s => s.StartsWith("position ") && s.Contains("e2e4")), Arg.Any<CancellationToken>());
        }
        finally
        {
            if (reader is not null) await reader.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
