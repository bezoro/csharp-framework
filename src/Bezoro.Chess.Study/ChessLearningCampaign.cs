using System.Collections.ObjectModel;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Progression;

namespace Bezoro.Chess.Study;

/// <summary>One teaching phase, with its lesson and generic story prerequisite definition.</summary>
public sealed class ChessLearningStage
{
    /// <summary>Creates a one-completion stage. Progress and unlocking remain owned by <see cref="StoryProgress" />.</summary>
    public ChessLearningStage(ProgressionId id, GuidedLessonDefinition lesson,
        ReadOnlySpan<StoryPrerequisite> prerequisites = default)
    {
        Lesson = lesson ?? throw new ArgumentNullException(nameof(lesson));
        Progression = new StoryStageDefinition(id, maximumStars: 1, prerequisites);
    }

    /// <summary>Gets the stable stage ID used by story progress.</summary>
    public ProgressionId Id => Progression.Id;
    /// <summary>Gets the immutable lesson, including its independent teaching kind and play mode.</summary>
    public GuidedLessonDefinition Lesson { get; }
    /// <summary>Gets the existing story module's prerequisite definition.</summary>
    public StoryStageDefinition Progression { get; }
    /// <summary>Gets the earlier fundamental lesson IDs reinforced by this phase.</summary>
    public IReadOnlyList<string> ReinforcedFundamentals => Lesson.Fundamentals;
}

/// <summary>
/// Immutable lessons connected to a validated story graph. Owns no player progress, match session, engine or UI.
/// Applications may localize titles and instructions by stable lesson ID and step index.
/// </summary>
public sealed class ChessLearningCampaign
{
    private readonly Dictionary<ProgressionId, ChessLearningStage> _byStage = new();
    private readonly Dictionary<string, ChessLearningStage> _byLesson = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies and validates the stages and story graph. A boss must reinforce known fundamental lessons that occur
    /// in its prerequisite closure; unrelated or later lessons cannot satisfy its teaching prerequisites.
    /// </summary>
    public ChessLearningCampaign(string id, IEnumerable<ChessLearningStage> stages)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A campaign ID is required.", nameof(id));
        if (stages is null) throw new ArgumentNullException(nameof(stages));
        var copied = stages.ToArray();
        if (copied.Length == 0) throw new ArgumentException("A campaign requires at least one stage.", nameof(stages));
        foreach (var stage in copied)
        {
            if (stage is null) throw new ArgumentException("Campaign stages cannot be null.", nameof(stages));
            var validation = GuidedLessonValidator.Validate(stage.Lesson);
            if (validation.HasErrors) throw new ArgumentException(string.Join("\n", validation.Messages), nameof(stages));
            if (_byStage.ContainsKey(stage.Id) || _byLesson.ContainsKey(stage.Lesson.Id.Trim()))
                throw new ArgumentException("Campaign stage and lesson IDs must be unique.", nameof(stages));
            _byStage.Add(stage.Id, stage);
            _byLesson.Add(stage.Lesson.Id.Trim(), stage);
        }
        Story = new StoryCatalog(copied.Select(stage => stage.Progression).ToArray());
        foreach (var stage in copied)
        {
            if (stage.Lesson.Kind != GuidedLessonKind.Boss) continue;
            if (stage.ReinforcedFundamentals.Count == 0)
                throw new ArgumentException("A boss must reinforce at least one earlier fundamental.", nameof(stages));
            var ancestors = GetPrerequisiteClosure(stage.Id);
            foreach (string fundamentalId in stage.ReinforcedFundamentals)
                if (!_byLesson.TryGetValue(fundamentalId.Trim(), out var fundamental) ||
                    fundamental.Lesson.Kind != GuidedLessonKind.Fundamental || !ancestors.Contains(fundamental.Id))
                    throw new ArgumentException("Boss fundamentals must be fundamental lessons in its prerequisite closure.", nameof(stages));
        }
        Id = id;
        Stages = new ReadOnlyCollection<ChessLearningStage>(copied);
    }

    /// <summary>Gets the application-selected campaign identity.</summary>
    public string Id { get; }
    /// <summary>Gets immutable stages in authored display order.</summary>
    public IReadOnlyList<ChessLearningStage> Stages { get; }
    /// <summary>Gets the existing story module's authoritative unlock graph.</summary>
    public StoryCatalog Story { get; }

    /// <summary>Gets a stage by progression ID, or throws when the ID is unknown.</summary>
    public ChessLearningStage GetStage(ProgressionId id) => _byStage.TryGetValue(id, out var stage)
        ? stage : throw new ArgumentException("The stage does not belong to this campaign.", nameof(id));

    /// <summary>Gets a stage by lesson ID, or throws when the ID is unknown.</summary>
    public ChessLearningStage GetStage(string lessonId)
    {
        if (lessonId is null) throw new ArgumentNullException(nameof(lessonId));
        return _byLesson.TryGetValue(lessonId.Trim(), out var stage)
            ? stage : throw new ArgumentException("The lesson does not belong to this campaign.", nameof(lessonId));
    }

    /// <summary>
    /// Creates an introductory campaign of seventeen cumulative phases. Scripted bosses reinforce earlier mechanics;
    /// a separate goal puzzle requires checkmate within three learner moves. English text is a localizable default.
    /// </summary>
    public static ChessLearningCampaign CreateFoundations()
    {
        var stages = new List<ChessLearningStage>(17);
        const string prefix = "foundations.";
        void Add(string key, string title, string fen, GuidedLessonStep[] steps,
            GuidedLessonKind kind = GuidedLessonKind.Fundamental, string[]? fundamentals = null,
            ChessPuzzleDefinition? puzzle = null)
        {
            int number = stages.Count + 1;
            var lesson = new GuidedLessonDefinition(prefix + key, title,
                new PlayableMatchSetup(Fen.Parse(fen)!.Value), steps,
                category: kind == GuidedLessonKind.Boss ? GuidedLessonCategory.Challenge : GuidedLessonCategory.Rules,
                order: number, kind: kind, fundamentals: fundamentals,
                mode: puzzle is null ? GuidedLessonMode.Scripted : GuidedLessonMode.GoalPuzzle, puzzle: puzzle);
            var requirements = number == 1 ? Array.Empty<StoryPrerequisite>()
                : new[] { new StoryPrerequisite(stages[number - 2].Id) };
            stages.Add(new ChessLearningStage(new ProgressionId((uint)number), lesson, requirements));
        }
        static GuidedLessonStep Move(string instruction, string move, string? reply = null,
            GuidedLessonObjective objective = GuidedLessonObjective.MovePiece) =>
            new(instruction: instruction, expectedPlayerMove: move, opponentReply: reply, objective: objective);

        Add("pawn", "The pawn", "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1", new[]
        {
            Move("From its starting rank a pawn may advance two empty squares. Play e2 to e4.", "e2e4", "e8d8"),
            Move("After leaving its starting rank, the pawn advances one square. Play e4 to e5.", "e4e5")
        });
        Add("rook", "The rook, blockers and captures", "7k/8/8/8/7p/P7/1K6/R7 w - - 0 1", new[]
        {
            Move("The rook moves along ranks and files. The pawn on a3 blocks its file. Play a1 to h1.", "a1h1", "h8g8"),
            Move("A rook stops on the captured piece's square. Capture the pawn on h4.", "h1h4", objective: GuidedLessonObjective.Capture)
        });
        Add("bishop", "The bishop", "7k/8/8/8/5P2/p7/8/2B4K w - - 0 1", new[]
        {
            Move("A bishop stays on diagonals and cannot jump over its own pawn. Capture c1 to a3.", "c1a3", "h8g8", GuidedLessonObjective.Capture),
            Move("Continue on the open diagonal from a3 to e7.", "a3e7")
        });
        Add("boss-lines", "Boss: open the line", "7k/8/8/8/5p2/8/3P4/R1B4K w - - 0 1", new[]
        {
            Move("Open the bishop's diagonal by advancing d2 to d4.", "d2d4", "h8g8"),
            Move("Use the opened diagonal to capture c1 to f4.", "c1f4", "g8h8", GuidedLessonObjective.Capture),
            Move("Finish by moving the rook along its open file from a1 to a4.", "a1a4")
        }, GuidedLessonKind.Boss, new[] { prefix + "pawn", prefix + "rook", prefix + "bishop" });
        Add("knight", "The knight", "7k/8/8/3P1p2/2PNP3/3P4/8/K7 w - - 0 1", new[]
        {
            Move("The knight jumps over adjacent pieces in an L shape. Capture d4 to f5.", "d4f5", "h8g8", GuidedLessonObjective.Capture),
            Move("Make another L-shaped jump from f5 to h4.", "f5h4")
        });
        Add("queen", "The queen", "8/7k/8/8/3p4/8/8/K2Q4 w - - 0 1", new[]
        {
            Move("The queen combines rook and bishop movement. Capture along the file from d1 to d4.", "d1d4", "h7g6", GuidedLessonObjective.Capture),
            Move("Now use a diagonal from d4 to a7.", "d4a7")
        });
        Add("king", "The king", "4k3/8/8/8/8/8/P3K3/8 w - - 0 1", new[]
        {
            Move("The king moves one square and must stay safe. Move diagonally from e2 to f3.", "e2f3", "e8e7"),
            Move("Move one square along the rank from f3 to e3.", "f3e3")
        });
        Add("boss-pieces", "Boss: coordinate the pieces", "7k/7p/8/8/8/2p5/4P3/RNBQK3 w - - 0 1", new[]
        {
            Move("Jump with the knight to capture the pawn on c3.", "b1c3", "h8g8", GuidedLessonObjective.Capture),
            Move("Centralize the queen from d1 to d3.", "d1d3", "g8h8"),
            Move("Advance the pawn from e2 to e4.", "e2e4", "h8g8"),
            Move("Occupy the freed square with the king from e1 to e2.", "e1e2")
        }, GuidedLessonKind.Boss, new[] { prefix + "pawn", prefix + "knight", prefix + "queen", prefix + "king" });
        Add("check", "Check", "7k/8/8/8/8/8/8/R3K3 w - - 0 1", new[]
        {
            Move("Check attacks the king. Move the rook from a1 to a8 to give check.", "a1a8", objective: GuidedLessonObjective.GiveCheck)
        });
        Add("escape-check", "Escape from check", "4r2k/8/8/8/8/8/R7/4K3 w - - 0 1", new[]
        {
            Move("Your king is in check. Block the attacking file with the rook from a2 to e2.", "a2e2", objective: GuidedLessonObjective.EscapeCheck)
        });
        Add("checkmate", "Checkmate", "7k/8/5KQ1/8/8/8/8/8 w - - 0 1", new[]
        {
            Move("The king protects the queen. Move g6 to g7 to leave the opposing king no legal escape.", "g6g7", objective: GuidedLessonObjective.Checkmate)
        });
        Add("boss-checkmate", "Boss: mate within three moves", "7k/8/8/5K2/8/8/Q7/8 w - - 0 1",
            Array.Empty<GuidedLessonStep>(), GuidedLessonKind.Boss,
            new[] { prefix + "queen", prefix + "king", prefix + "check", prefix + "checkmate" },
            new ChessPuzzleDefinition(3, "Find checkmate within three of your moves. Any legal move is allowed; the opponent defends."));
        Add("castle-kingside", "Kingside castling", "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1", new[]
        {
            Move("With the path empty and safe, move the king from e1 to g1. The rook moves to f1.", "e1g1", objective: GuidedLessonObjective.Castle)
        });
        Add("castle-queenside", "Queenside castling", "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1", new[]
        {
            Move("Castle on the other side: e1 to c1. The rook moves from a1 to d1; all squares between must be empty.", "e1c1", objective: GuidedLessonObjective.Castle)
        });
        Add("en-passant", "En passant", "4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 2", new[]
        {
            Move("The opposing pawn just advanced two squares. Capture e5 to d6 now; the pawn on d5 is removed.", "e5d6", objective: GuidedLessonObjective.Capture)
        });
        Add("promotion", "Pawn promotion", "1r5k/P7/8/8/8/8/8/7K w - - 0 1", new[]
        {
            Move("A pawn reaching the last rank chooses queen, rook, bishop or knight. Capture a7 to b8 and choose queen.", "a7b8q", objective: GuidedLessonObjective.Promote)
        });
        Add("boss-special-moves", "Boss: remember the special rules", "1r5k/P5pp/8/3pP3/8/8/7P/RNBQK2R w K d6 0 2", new[]
        {
            Move("Use the immediate en-passant opportunity from e5 to d6.", "e5d6", "g7g6", GuidedLessonObjective.Capture),
            Move("Castle safely from e1 to g1.", "e1g1", "g6g5", GuidedLessonObjective.Castle),
            Move("Capture a7 to b8 and promote to a queen.", "a7b8q", objective: GuidedLessonObjective.Promote)
        }, GuidedLessonKind.Boss, new[] { prefix + "pawn", prefix + "rook", prefix + "king", prefix + "castle-kingside", prefix + "en-passant", prefix + "promotion" });
        return new ChessLearningCampaign("chess-foundations-v1", stages);
    }

    private HashSet<ProgressionId> GetPrerequisiteClosure(ProgressionId stage)
    {
        var found = new HashSet<ProgressionId>();
        var pending = new Stack<ProgressionId>();
        pending.Push(stage);
        while (pending.Count != 0)
            foreach (var requirement in Story.GetDefinition(pending.Pop()).Prerequisites)
                if (found.Add(requirement.Stage)) pending.Push(requirement.Stage);
        return found;
    }
}
