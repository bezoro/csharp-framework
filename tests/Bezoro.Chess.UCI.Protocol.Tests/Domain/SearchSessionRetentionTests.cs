using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;
using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain;

public sealed class SearchSessionRetentionTests
{
    [Fact]
    public void Search_RetainsRecentVariationsWithinBudgetAndPreservesCumulativeCounters()
    {
        var session = new SearchSession(false, maxCapturedVariations: 2);
        for (var depth = 1; depth <= 4; depth++)
        {
            UciInfoParser.TryParseTrimmed($"info depth {depth} nodes {depth * 10} score cp {depth} pv e2e4", out var info)
                .Should().BeTrue();
            session.RecordInfo(info);
        }
        var result = session.BuildResult(new("e2e4", "", "bestmove e2e4"));
        result.PrincipalVariations.Select(pv => pv.Depth).Should().Equal(3u, 4u);
        result.ReachedDepth.Should().Be(4);
        result.TotalNodesSearched.Should().Be(40);
        result.BestCpScore.Should().Be(4);
    }
}
