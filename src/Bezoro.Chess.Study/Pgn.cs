using System.Globalization;
using System.Text;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>Imports and exports one standard-chess PGN game with tags, SAN, comments, NAGs and nested variations.</summary>
public static class Pgn
{
    private static readonly string[] Roster = { "Event", "Site", "Date", "Round", "White", "Black", "Result" };

    /// <summary>
    /// Imports and validates the entire game locally. Illegal moves, inconsistent results, malformed input and
    /// budget overruns throw FormatException; no partially imported study is returned. The cursor ends at the root.
    /// </summary>
    public static ChessStudy Parse(string text, PgnReadLimits? limits = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        limits ??= new PgnReadLimits();
        if (text.Length > limits.MaxInputCharacters) throw new FormatException("PGN input limit exceeded.");
        var reader = new Reader(text, limits.MaxTokenCharacters);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        Token token = reader.Read();
        while (token.Kind == '[')
        {
            Token name = reader.Read(), value = reader.Read(), closing = reader.Read();
            if (name.Kind != 's' || value.Kind != 'q' || closing.Kind != ']') throw new FormatException("Invalid PGN tag pair.");
            if (tags.ContainsKey(name.Text)) throw new FormatException("Duplicate PGN tag.");
            tags.Add(name.Text, value.Text);
            token = reader.Read();
        }
        Fen start = Fen.Default;
        bool hasSetup = tags.TryGetValue("SetUp", out string? setup);
        bool hasFen = tags.TryGetValue("FEN", out string? rawFen);
        if (hasSetup && setup is not ("0" or "1")) throw new FormatException("SetUp must be 0 or 1.");
        if (setup == "1" && !hasFen || hasFen && setup != "1") throw new FormatException("A custom starting position requires SetUp 1 and FEN.");
        if (hasFen) start = Fen.Parse(rawFen!) ?? throw new FormatException("Invalid starting FEN.");
        if (tags.TryGetValue("Variant", out string? variant) && variant != "Standard")
            throw new FormatException("Only standard chess PGN is supported.");
        ChessStudy study;
        try { study = new ChessStudy(start, limits.MaxNodes); }
        catch (ArgumentException exception) { throw new FormatException("Invalid playable starting position.", exception); }
        foreach (var tag in tags)
        {
            if (tag.Key is "SetUp" or "FEN") continue;
            try { study.SetTag(tag.Key, tag.Value); }
            catch (ArgumentException exception) { throw new FormatException("Invalid PGN tag.", exception); }
        }
        var variations = new Stack<(StudyNode Resume, StudyNode Start)>();
        bool canVary = false, finished = false;
        string leadingComment = string.Empty;
        while (token.Kind != '\0')
        {
            if (finished) throw new FormatException("Unexpected data after PGN result; import one game at a time.");
            switch (token.Kind)
            {
                case 'c':
                    if (!canVary && variations.Count != 0)
                        leadingComment = AppendComment(leadingComment, token.Text, limits.MaxTokenCharacters);
                    else study.SetComment(study.Current, AppendComment(study.Current.Comment, token.Text, limits.MaxTokenCharacters));
                    break;
                case '(':
                    if (!canVary || study.Current.Parent is null) throw new FormatException("A variation must replace a preceding move.");
                    if (variations.Count >= limits.MaxVariationDepth) throw new FormatException("PGN variation depth limit exceeded.");
                    variations.Push((study.Current, study.Current.Parent));
                    study.Undo();
                    canVary = false;
                    break;
                case ')':
                    if (variations.Count == 0) throw new FormatException("Unexpected variation closing parenthesis.");
                    var frame = variations.Pop();
                    if (ReferenceEquals(frame.Start, study.Current)) throw new FormatException("Empty PGN variation.");
                    study.GoTo(frame.Resume);
                    canVary = true;
                    break;
                case '$':
                    if (!canVary || !byte.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out byte glyph))
                        throw new FormatException("Invalid numeric annotation glyph.");
                    study.AddAnnotation(study.Current, glyph);
                    break;
                case 's':
                    string symbol = StripMoveNumber(token.Text);
                    if (symbol.Length == 0) break;
                    if (symbol is "*" or "1-0" or "0-1" or "1/2-1/2")
                    {
                        if (variations.Count != 0) throw new FormatException("A game result cannot terminate a variation.");
                        if (tags.TryGetValue("Result", out string? result) && result != symbol) throw new FormatException("PGN tag and movetext results differ.");
                        study.SetTag("Result", symbol);
                        finished = true;
                        break;
                    }
                    byte? suffixGlyph = StripGlyph(ref symbol);
                    try { study.ImportSan(symbol); }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
                    { throw new FormatException("Invalid PGN move: " + token.Text, exception); }
                    if (suffixGlyph.HasValue) study.AddAnnotation(study.Current, suffixGlyph.Value);
                    if (leadingComment.Length != 0)
                    {
                        study.SetLeadingComment(study.Current, leadingComment);
                        leadingComment = string.Empty;
                    }
                    canVary = true;
                    break;
                default: throw new FormatException("Unexpected PGN token.");
            }
            token = reader.Read();
        }
        if (variations.Count != 0) throw new FormatException("Unclosed PGN variation.");
        if (!finished) throw new FormatException("PGN game termination marker is required.");
        study.GoTo(study.Root);
        return study;
    }

    /// <summary>Exports the entire tree without changing its cursor. Missing roster tags receive PGN unknown values.</summary>
    public static string Write(ChessStudy study)
    {
        if (study is null) throw new ArgumentNullException(nameof(study));
        var output = new StringBuilder();
        foreach (string name in Roster)
            WriteTag(output, name, name == "Result" ? study.Result : study.Tags.TryGetValue(name, out string? value) ? value : name == "Date" ? "????.??.??" : "?");
        foreach (var tag in study.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!Roster.Contains(tag.Key)) WriteTag(output, tag.Key, tag.Value);
        if (study.Root.Position.Raw != Fen.Default.Raw)
        {
            WriteTag(output, "SetUp", "1");
            WriteTag(output, "FEN", study.Root.Position.Raw);
        }
        output.Append('\n');
        WriteComment(output, study.Root.Comment);
        var work = new Stack<(StudyNode? Node, char Punctuation, bool Siblings)>();
        if (study.Root.VariationCount != 0) work.Push((study.Root.GetVariation(0), '\0', true));
        while (work.Count != 0)
        {
            var item = work.Pop();
            if (item.Node is not { } node) { output.Append(item.Punctuation).Append(' '); continue; }
            Fen parent = node.Parent!.Position;
            WriteComment(output, node.LeadingComment);
            output.Append(parent.FullmoveNumber.ToString(CultureInfo.InvariantCulture))
                .Append(parent.ActiveColor == 'w' ? ". " : "... ").Append(node.San).Append(' ');
            for (int i = 0; i < node.AnnotationCount; i++) output.Append('$').Append(node.GetAnnotation(i).ToString(CultureInfo.InvariantCulture)).Append(' ');
            WriteComment(output, node.Comment);
            if (node.VariationCount != 0) work.Push((node.GetVariation(0), '\0', true));
            if (!item.Siblings) continue;
            for (int i = node.Parent.VariationCount - 1; i >= 1; i--)
            {
                work.Push((null, ')', false));
                work.Push((node.Parent.GetVariation(i), '\0', false));
                work.Push((null, '(', false));
            }
        }
        output.Append(study.Result).Append('\n');
        return output.ToString();
    }

    private static void WriteTag(StringBuilder output, string name, string value) =>
        output.Append('[').Append(name).Append(" \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"]\n");

    private static void WriteComment(StringBuilder output, string comment)
    {
        if (comment.Length == 0) return;
        if (comment.AsSpan().IndexOfAny('{', '}') < 0) output.Append('{').Append(comment).Append("} ");
        else output.Append(';').Append(comment.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n;")).Append('\n');
    }
    private static string AppendComment(string previous, string next, int maxCharacters)
    {
        if ((long)previous.Length + next.Length + (previous.Length == 0 ? 0 : 1) > maxCharacters)
            throw new FormatException("PGN combined comment limit exceeded.");
        return previous.Length == 0 ? next : previous + "\n" + next;
    }

    private static string StripMoveNumber(string symbol)
    {
        int index = 0;
        while (index < symbol.Length && symbol[index] is >= '0' and <= '9') index++;
        if (index == 0 || index == symbol.Length || symbol[index] != '.') return symbol;
        int start = index;
        while (index < symbol.Length && symbol[index] == '.') index++;
        if (index - start is not (1 or 3)) throw new FormatException("Invalid PGN move number.");
        return symbol.Substring(index);
    }
    private static byte? StripGlyph(ref string symbol)
    {
        int index = symbol.Length;
        while (index > 0 && symbol[index - 1] is '!' or '?') index--;
        if (index == symbol.Length) return null;
        byte glyph = symbol.Substring(index) switch
        {
            "!" => 1, "?" => 2, "!!" => 3, "??" => 4, "!?" => 5, "?!" => 6,
            _ => throw new FormatException("Invalid symbolic annotation glyph.")
        };
        symbol = symbol.Substring(0, index);
        return glyph;
    }

    private readonly struct Token(char kind, string text)
    {
        internal char Kind { get; } = kind;
        internal string Text { get; } = text;
    }

    private sealed class Reader(string text, int maxTokenCharacters)
    {
        private int _offset;
        internal Token Read()
        {
            while (_offset < text.Length)
            {
                char next = text[_offset];
                if (char.IsWhiteSpace(next) || _offset == 0 && next == '\uFEFF') { _offset++; continue; }
                if (next == '%' && (_offset == 0 || text[_offset - 1] is '\r' or '\n'))
                { while (_offset < text.Length && text[_offset] is not ('\r' or '\n')) _offset++; continue; }
                break;
            }
            if (_offset == text.Length) return new Token('\0', string.Empty);
            char c = text[_offset++];
            if (c is '[' or ']' or '(' or ')') return new Token(c, string.Empty);
            int start = _offset;
            if (c is '{' or ';')
            {
                while (_offset < text.Length && (c == '{' ? text[_offset] != '}' : text[_offset] is not ('\r' or '\n'))) _offset++;
                if (c == '{' && _offset == text.Length) throw new FormatException("Unclosed PGN comment.");
                string comment = Slice(start);
                if (c == '{') _offset++;
                if (comment.IndexOf('\0') >= 0 || c == '{' && comment.IndexOf('{') >= 0) throw new FormatException("Invalid PGN comment.");
                return new Token('c', comment);
            }
            if (c == '"')
            {
                var value = new StringBuilder();
                while (_offset < text.Length)
                {
                    char ch = text[_offset++];
                    if (ch == '"') return new Token('q', value.ToString());
                    if (ch == '\\')
                    {
                        if (_offset == text.Length || text[_offset] is not ('\\' or '"')) throw new FormatException("Invalid PGN string escape.");
                        ch = text[_offset++];
                    }
                    if (char.IsControl(ch)) throw new FormatException("Control character in PGN tag.");
                    if (value.Length == maxTokenCharacters) throw new FormatException("PGN token limit exceeded.");
                    value.Append(ch);
                }
                throw new FormatException("Unclosed PGN tag string.");
            }
            if (c != '$') start--;
            while (_offset < text.Length && !char.IsWhiteSpace(text[_offset]) && "[](){};\"$".IndexOf(text[_offset]) < 0) _offset++;
            return new Token(c == '$' ? '$' : 's', Slice(start));
        }
        private string Slice(int start)
        {
            int length = _offset - start;
            if (length > maxTokenCharacters) throw new FormatException("PGN token limit exceeded.");
            return text.Substring(start, length);
        }
    }
}
