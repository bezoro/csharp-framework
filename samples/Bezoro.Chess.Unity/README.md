# Unity board presenter sample

Copy these C# 9 scripts into a Unity 6 project consuming the compiled framework package. Add `ChessBoardPresenter` to a persistent scene object. Assign two distinct child objects with your sprite and model implementations of the abstract `ChessBoardRenderer`; the presenter must remain active when either child is hidden. The scripts contain no art assets, camera or pointer implementation. They are an application adapter, not a complete scene.

The renderer receives all pieces and highlight flags through the same immutable `ChessBoardPresentation`. It should reuse its 64 cell objects and piece pool, convert palette channels to `UnityEngine.Color32`, and map board coordinates using `ChessBoardCoordinates`. Its hit test forwards a picked cell to `presenter.SelectCell(column, row)`. Rows increase from the bottom of the board. Its optional `Animate` override uses the canonical event to distinguish forward moves from undo/load. Use `MoveData.CapturedPiece`, secondary rook motion and `PreviousFen`/`ResultingFen` to animate special moves, or `ChessBoardPresentation.LastMove` for the reconciled forward geometry.

A local controller can connect as follows (error handling and lifetime belong to that controller):

```csharp
var match = new UciPlayableMatchSession();
var initial = match.Refresh();
presenter.Present(in initial);

presenter.MoveRequested += move =>
{
    match.ApplyMove(move);
    var current = match.Refresh();
    presenter.Present(in current);
};
presenter.PromotionChosen += piece =>
{
    match.ChoosePromotion(piece);
    var current = match.Refresh();
    presenter.Present(in current);
};
presenter.PromotionChanged += request =>
{
    // Show/hide a panel with the request's AllowedPromotionPieces.
};
```

For animation, retain the corresponding canonical event during command processing and pass it once as the second `Present` argument after refreshing. Do not call Unity renderers from match callbacks on arbitrary worker threads. Marshal network responses and engine results to Unity's main thread first. A network controller handles `MoveRequested` by submitting to the server and presenting only the accepted authoritative state; set `InteractionEnabled = false` while waiting if further input should be suppressed.

Connect buttons to `SetVisualMode`, `SetOrientation`, `ChoosePromotion` and `SetSkin`. The selected skin supplies a full sprite-set key and a full model-set key. Authorize ownership through the player's store/profile before calling `SetSkin`; preloading and releasing actual assets belongs to the application's asset manager. Switching renderer, palette or orientation issues no match commands or engine requests.

There is no `Update` method. Clock display and timeout polling, if enabled, should use the application's chosen scheduler rather than rebuilding the board every frame. Dispose the local match when its controller ends and unsubscribe callbacks when the view/controller is destroyed.

The source is checked with a C# 9 compiler and a minimal Unity API stub as a compile guard. No Unity Editor, scene, Mono player or IL2CPP execution is available in this repository's command-line environment; build and profile the sample inside the actual target Unity project before shipping.
