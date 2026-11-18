using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

[TestSubject(typeof(UciEngineClient))]
public class UciEngineClientMetadataTests
{
	[Fact]
	public async Task Metadata_IsBoundedOrderedAndClosedAtUciOkWhileProtocolEventsContinue()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		await using var client = new UciEngineClient(transport, new() { MaxAdvertisedOptions = 2 });
		int optionEvents = 0;
		client.ProtocolMessageReceived += message =>
		{
			if (message.Option.HasValue) Interlocked.Increment(ref optionEvents);
		};
		transport.WriteLineAsync("uci", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			foreach (string line in new[]
			{
				"id name First", "option name One type string default first", "option name Two type button",
				"option name Overflow type button", "option name ONE type string default updated", "uciok",
				"option name Two type string default late", "id name Late"
			}) channel.Writer.TryWrite(line);
			return Task.CompletedTask;
		});
		transport.WriteLineAsync("isready", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			channel.Writer.TryWrite("readyok");
			return Task.CompletedTask;
		});
		await client.StartAsync();
		Assert.Equal("First", client.EngineInfo.Name);
		Assert.Collection(client.AvailableOptions,
			option => { Assert.Equal("ONE", option.Name); Assert.Equal("updated", option.DefaultValue); },
			option => { Assert.Equal("Two", option.Name); Assert.Equal(UciOptionType.Button, option.Type); });
		Assert.Equal(5, optionEvents);
	}

	[Fact]
	public async Task Rehandshake_PublishesOnlyCompletedMetadataAndQueuedCancellationKeepsPriorSnapshot()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		await using var client = new UciEngineClient(transport);
		var secondIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int count = 0;
		transport.WriteLineAsync("uci", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			if (++count == 1)
			{
				channel.Writer.TryWrite("option name Old type button");
				channel.Writer.TryWrite("uciok");
			}
			else
			{
				channel.Writer.TryWrite("option name New type button");
				secondIssued.TrySetResult();
			}
			return Task.CompletedTask;
		});
		transport.WriteLineAsync("isready", Arg.Any<CancellationToken>()).Returns(_ =>
		{
			channel.Writer.TryWrite("readyok");
			return Task.CompletedTask;
		});
		await client.StartAsync();
		var original = client.AvailableOptions;
		var pending = client.UciInitAsync(default);
		await secondIssued.Task.WaitAsync(TestConstants.DefaultTimeout);
		using var queuedCancellation = new CancellationTokenSource();
		var queued = client.UciInitAsync(queuedCancellation.Token);
		queuedCancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
		Assert.Equal(original, client.AvailableOptions);
		channel.Writer.TryWrite("uciok");
		await pending;
		Assert.Equal("New", Assert.Single(client.AvailableOptions).Name);
		Assert.Equal("Old", Assert.Single(original).Name);
	}

	[Fact]
	public async Task StartAsync_WhenHandshakeEmitsIdAndOptionLines_ShouldCaptureEngineMetadata()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var client = new UciEngineClient(transport);

		transport.When(x => x.WriteLineAsync("uci", Arg.Any<CancellationToken>()))
				 .Do(async _ =>
					 {
						 await channel.Writer.WriteAsync("id name Test Engine 1.0");
						 await channel.Writer.WriteAsync("id author Bezoro");
						 await channel.Writer.WriteAsync(
							 "option name Hash type spin default 16 min 1 max 4096"
						 );

						 await channel.Writer.WriteAsync(
							 "option name Ponder type check default false"
						 );

						 await channel.Writer.WriteAsync(
							 "option name Style type combo default Normal var Normal var Risky"
						 );

						 await channel.Writer.WriteAsync("uciok");
					 }
				 );

		transport.When(x => x.WriteLineAsync("isready", Arg.Any<CancellationToken>()))
				 .Do(_ => channel.Writer.TryWrite("readyok"));

		await client.StartAsync(CancellationToken.None);

		client.EngineInfo.Name.Should().Be("Test Engine 1.0");
		client.EngineInfo.Author.Should().Be("Bezoro");
		client.AvailableOptions.Should().ContainSingle(x =>
														   x.Name == "Hash" &&
														   x.Type == UciOptionType.Spin &&
														   x.DefaultValue == "16" &&
														   x.Min == 1 &&
														   x.Max == 4096
		);

		client.AvailableOptions.Should().ContainSingle(x =>
														   x.Name == "Style" &&
														   x.Type == UciOptionType.Combo &&
														   x.DefaultValue == "Normal" &&
														   x.Variables.SequenceEqual(new[] { "Normal", "Risky" })
		);

		client.Capabilities.DebugCommand.Should().Be(UciCapabilityState.Supported);
		client.Capabilities.RegisterCommand.Should().Be(UciCapabilityState.Supported);
		client.Capabilities.PonderHit.Should().Be(UciCapabilityState.Supported);

		await client.DisposeAsync();
	}
}
