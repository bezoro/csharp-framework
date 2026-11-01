using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using FluentAssertions;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public sealed class UciCommandValidationTests
{
    [Fact]
    public async Task InvalidAnalysisRequest_PerformsNoEngineWork()
    {
        var (transport, client) = UciEngineClientTestHelpers.CreateClientWithTransport();
        await using var ownedClient = client;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.EvaluateAdvantageAsync('x', 'w'));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.AnalyzePositionAsync('w', 'w', ["e2e4"], fallbackMoveTimeMs: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AnalyzePositionAsync('w', 'w', ["bad"]));
        await transport.DidNotReceiveWithAnyArgs().WriteLineAsync(default!, default);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData(null)]
    public void SearchMoves_MalformedEntryRejectsWholeSearch(string? invalidMove)
    {
        var parameters = new SearchParameters { Depth = 1, SearchMoves = ["e2e4", invalidMove!] };
        Action validate = parameters.Validate;
        Action build = () => UciEngineClient.BuildGoCommand(parameters);
        validate.Should().Throw<ArgumentException>();
        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PositionHistory_MalformedEntryDoesNotSendTruncatedHistory()
    {
        var (transport, client) = UciEngineClientTestHelpers.CreateClientWithTransport();
        await using var ownedClient = client;
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetPositionAsync(Fen.Default, ["e2e4", "bad", "e7e5"], default));
        await transport.DidNotReceiveWithAnyArgs().WriteLineAsync(default!, default);
    }

    [Theory]
    [InlineData("\nquit")]
    [InlineData("\rstop")]
    [InlineData("\0")]
    public void TypedCommandFields_RejectAdditionalLinesAndNul(string suffix)
    {
        Action optionName = () => UciCommandBuilder.BuildSetOptionCommand("Hash" + suffix, "16");
        Action optionValue = () => UciCommandBuilder.BuildSetOptionCommand("Hash", "16" + suffix);
        Action registrationName = () => UciCommandBuilder.BuildRegisterCommand(new(false, "name" + suffix, "code"));
        Action registrationCode = () => UciCommandBuilder.BuildRegisterCommand(new(false, "name", "code" + suffix));
        optionName.Should().Throw<ArgumentException>();
        optionValue.Should().Throw<ArgumentException>();
        registrationName.Should().Throw<ArgumentException>();
        registrationCode.Should().Throw<ArgumentException>();
    }
}
