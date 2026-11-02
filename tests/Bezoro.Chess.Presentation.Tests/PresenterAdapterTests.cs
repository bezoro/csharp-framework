using System.Reflection;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Chess.UnitySample;
using Xunit;

namespace Bezoro.Chess.Presentation.Tests;

// These exercise the adapter's command/render wiring with a compile-time Unity surface.
// They do not simulate Unity's scene lifecycle, rendering or asset-loading behavior.
public sealed class PresenterAdapterTests
{
    [Fact]
    public void RendererSwitch_ReusesAuthoritativePositionAndChangesOnlyVisibilityAndAssetKey()
    {
        using var match = new UciPlayableMatchSession();
        var state = match.Refresh();
        var two = new RecordingRenderer();
        var three = new RecordingRenderer();
        var presenter = Create(two, three);
        presenter.Present(in state);
        Assert.Equal(state.PositionKey, two.LastBoard.PositionKey);
        Assert.True(two.gameObject.activeSelf);
        Assert.False(three.gameObject.activeSelf);

        presenter.SetSkin(new ChessPieceSkin("marble", "marble/2d", "marble/3d"));
        presenter.SetVisualMode(ChessBoardVisualMode.ThreeDimensional);
        Assert.Equal(state.PositionKey, three.LastBoard.PositionKey);
        Assert.Equal("marble/3d", three.AssetKey);
        Assert.False(two.gameObject.activeSelf);
        Assert.True(three.gameObject.activeSelf);
        Assert.Equal(state, match.CurrentState);
    }

    [Fact]
    public void PointerSelection_OnlySubmitsLegalDestinationsAndNeverMutatesTheMatch()
    {
        using var match = new UciPlayableMatchSession();
        var state = match.Refresh();
        var renderer = new RecordingRenderer();
        var presenter = Create(renderer, new RecordingRenderer());
        presenter.Present(in state);
        var moves = new List<string>();
        presenter.MoveRequested += moves.Add;
        presenter.SelectCell(4, 6); // Enemy pawn: no selection.
        presenter.SelectCell(4, 3); // Empty destination alone: no submission.
        Assert.Empty(moves);
        presenter.SelectCell(4, 1); // e2.
        presenter.SelectCell(4, 3); // e4.
        Assert.Equal(new[] { "e2e4" }, moves);
        Assert.Equal(state, match.CurrentState);
        presenter.InteractionEnabled = false;
        presenter.SelectCell(3, 1);
        presenter.SelectCell(3, 3);
        Assert.Single(moves);
    }

    [Fact]
    public async Task PromotionPanel_ChangesOnlyWhenThePendingChoiceChanges()
    {
        using var match = new UciPlayableMatchSession();
        await match.LoadMatchAsync(new(Fen.Parse("7k/P7/8/8/8/8/8/7K w - - 0 1")!.Value));
        var presenter = Create(new RecordingRenderer(), new RecordingRenderer());
        var state = match.Refresh();
        presenter.Present(in state);
        var requests = new List<PendingPromotionRequest?>();
        presenter.PromotionChanged += requests.Add;
        char? chosen = null;
        presenter.PromotionChosen += value => chosen = value;
        match.ApplyMove("a7a8");
        state = match.Refresh();
        presenter.Present(in state);
        presenter.Present(in state);
        Assert.Single(requests);
        Assert.NotNull(requests[0]);
        presenter.ChoosePromotion('N');
        Assert.Equal('n', chosen);
        Assert.NotNull(match.CurrentState.PendingPromotion); // Only authority can apply it.
        Assert.Throws<ArgumentException>(() => presenter.ChoosePromotion('k'));
        match.ChoosePromotion('n');
        state = match.Refresh();
        presenter.Present(in state);
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[1]);
    }

    private static ChessBoardPresenter Create(RecordingRenderer two, RecordingRenderer three)
    {
        var presenter = new ChessBoardPresenter();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ChessBoardPresenter).GetField("twoDimensionalRenderer", flags)!.SetValue(presenter, two);
        typeof(ChessBoardPresenter).GetField("threeDimensionalRenderer", flags)!.SetValue(presenter, three);
        return presenter;
    }

    private sealed class RecordingRenderer : ChessBoardRenderer
    {
        public ChessBoardPresentation LastBoard { get; private set; }
        public string? AssetKey { get; private set; }
        public override void Render(in ChessBoardPresentation board, ChessHighlightPalette palette,
            ChessBoardOrientation orientation, string pieceSetAssetKey)
        {
            LastBoard = board;
            AssetKey = pieceSetAssetKey;
        }
    }
}
