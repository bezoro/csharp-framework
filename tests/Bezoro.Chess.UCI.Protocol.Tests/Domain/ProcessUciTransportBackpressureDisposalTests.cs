using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using static Bezoro.Chess.UCI.Protocol.Tests.Domain.ProcessUciTransportTestHelpers;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public sealed class ProcessUciTransportBackpressureDisposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_WithBlockedStdinWrite_KillsChildAndJoinsWriter(bool sendQuit)
    {
        var transport = RunningProcess().WithSendQuitOnDispose(sendQuit)
            .WithQuitGracePeriod(TimeSpan.FromMilliseconds(50))
            .WithTeardownTimeout(TimeSpan.FromSeconds(2)).Build();
        await transport.StartAsync();
        var process = (Process)typeof(ProcessUciTransport)
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
        Task? disposing = null;
        try
        {
            // The child never reads stdin. This exceeds both the writer buffer and the OS pipe capacity.
            await transport.WriteLineAsync(new string('x', 2 * 1024 * 1024));
            await Task.Delay(100);
            disposing = transport.DisposeAsync().AsTask();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Status.Should().Be(TransportStatus.Disposed);
            transport.IsStarted.Should().BeFalse();
        }
        finally
        {
            // Keep a failed regression from leaving its child or blocked write behind.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            await transport.DisposeAsync();
        }
    }
}
