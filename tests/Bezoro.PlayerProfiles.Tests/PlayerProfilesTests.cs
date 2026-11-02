using Bezoro.PlayerProfiles;
using Xunit;

namespace Bezoro.PlayerProfiles.Tests;

public class PlayerProfilesTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static PlayerMatchRecord Match(string id = "match-1", PlayerMatchOutcome outcome = PlayerMatchOutcome.Win, string player = "alice") =>
        new(id, player, "bob", outcome, CompletedAt, "rapid", isRated: true, replayReference: "replays/1");

    [Fact]
    public void Profile_keeps_identity_separate_from_display_metadata()
    {
        var profile = new PlayerProfile("player-42", "Player Name", "portraits/knight");
        Assert.True(profile.IsValid);
        Assert.Equal("player-42", profile.PlayerId);
        Assert.Equal("Player Name", profile.DisplayName);
        Assert.Equal("portraits/knight", profile.PortraitId);
        Assert.False(default(PlayerProfile).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Profiles_reject_missing_identity(string? id) => Assert.Throws<ArgumentException>(() => new PlayerProfile(id!, "name"));

    [Fact]
    public void History_retries_are_idempotent_and_conflicting_results_do_not_overwrite()
    {
        var history = new PlayerMatchHistory("alice");
        Assert.Equal(MatchHistoryAppendResult.Added, history.Append(Match()));
        Assert.Equal(MatchHistoryAppendResult.AlreadyRecorded, history.Append(Match()));
        Assert.Equal(MatchHistoryAppendResult.ConflictingRecord, history.Append(Match(outcome: PlayerMatchOutcome.Loss)));
        Assert.Equal(1, history.Count);
        Assert.Equal(PlayerMatchOutcome.Win, history[0].Outcome);
        Assert.True(history.TryGet("match-1", out var found));
        Assert.Equal(Match(), found);
    }

    [Fact]
    public void Full_history_retains_duplicate_checks_and_old_records()
    {
        var history = new PlayerMatchHistory("alice", capacity: 1);
        history.Append(Match());
        Assert.Equal(MatchHistoryAppendResult.CapacityReached, history.Append(Match("match-2")));
        Assert.Equal(MatchHistoryAppendResult.AlreadyRecorded, history.Append(Match()));
        Assert.Equal(1, history.Count);
        Assert.False(history.TryGet("match-2", out _));
    }

    [Fact]
    public void History_rejects_other_players_default_records_and_self_matches()
    {
        var history = new PlayerMatchHistory("alice");
        Assert.Throws<ArgumentException>(() => history.Append(default));
        Assert.Throws<ArgumentException>(() => history.Append(Match(player: "eve")));
        Assert.Throws<ArgumentException>(() => new PlayerMatchRecord("a", "alice", "alice", PlayerMatchOutcome.Draw, CompletedAt, "rapid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerMatchHistory("alice", 0));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void History_copy_preserves_insertion_order_without_exposing_mutable_storage()
    {
        var history = new PlayerMatchHistory("alice");
        history.Append(Match("second"));
        history.Append(Match("first"));
        var copied = new PlayerMatchRecord[2];
        history.CopyTo(copied);
        Assert.Equal("second", copied[0].MatchId);
        Assert.Equal("first", copied[1].MatchId);
        copied[0] = default;
        Assert.Equal("second", history[0].MatchId);
        Assert.Throws<ArgumentException>(() => history.CopyTo(new PlayerMatchRecord[1]));
    }

    [Theory]
    [InlineData(PlayerMatchOutcome.Win, 1216, 1184)]
    [InlineData(PlayerMatchOutcome.Draw, 1200, 1200)]
    [InlineData(PlayerMatchOutcome.Loss, 1184, 1216)]
    public void Elo_updates_both_players_from_the_pre_match_pair(PlayerMatchOutcome result, double first, double second)
    {
        var pair = EloRatings.ApplyResult(new EloRating(1200, 3), new EloRating(1200, 6), result);
        Assert.Equal(first, pair.FirstPlayer.Value, 10);
        Assert.Equal(second, pair.SecondPlayer.Value, 10);
        Assert.Equal(4, pair.FirstPlayer.GamesPlayed);
        Assert.Equal(7, pair.SecondPlayer.GamesPlayed);
    }

    [Fact]
    public void Elo_handles_unequal_strength_and_independent_k_factors()
    {
        var low = new EloRating(1200);
        var high = new EloRating(1600);
        Assert.Equal(1.0 / 11, EloRatings.ExpectedScore(low, high), 12);
        Assert.Equal(10.0 / 11, EloRatings.ExpectedScore(high, low), 12);
        var result = EloRatings.ApplyResult(low, high, PlayerMatchOutcome.Win, 40, 10);
        Assert.Equal(1200 + 40 * 10.0 / 11, result.FirstPlayer.Value, 10);
        Assert.Equal(1600 - 10 * 10.0 / 11, result.SecondPlayer.Value, 10);
    }

    [Fact]
    public void Elo_extreme_finite_inputs_have_finite_expected_scores()
    {
        Assert.Equal(0, EloRatings.ExpectedScore(new EloRating(-double.MaxValue), new EloRating(double.MaxValue)));
        Assert.Equal(1, EloRatings.ExpectedScore(new EloRating(double.MaxValue), new EloRating(-double.MaxValue)));
        Assert.Equal(0.5, EloRatings.ExpectedScore(new EloRating(double.MaxValue), new EloRating(double.MaxValue)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Elo_rejects_nonfinite_ratings(double value) => Assert.Throws<ArgumentOutOfRangeException>(() => new EloRating(value));

    [Fact]
    public void Elo_rejects_invalid_policy_outcomes_and_exhausted_counters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EloRatings.ApplyResult(default, default, PlayerMatchOutcome.Win, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EloRatings.ApplyResult(default, default, (PlayerMatchOutcome)99));
        Assert.Throws<InvalidOperationException>(() => EloRatings.ApplyResult(new EloRating(1200, int.MaxValue), default, PlayerMatchOutcome.Win));
        Assert.Throws<OverflowException>(() => EloRatings.ApplyResult(new EloRating(double.MaxValue), new EloRating(double.MaxValue), PlayerMatchOutcome.Win, double.MaxValue));
    }
}
