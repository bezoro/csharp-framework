using System.Text;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.PlayerProfiles;
using Bezoro.Progression;

namespace Bezoro.Chess.Progression;

// The persistence envelope owns checksums/schema. This codec is bounded, explicit application data, never executable objects.
internal static class ProgressSaveCodec
{
    private const uint Magic = 0x31504342;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] Encode(ProgressState state)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        Text(writer, state.Catalog.Fingerprint);
        writer.Write(state.Options.InitialTrainingRating); writer.Write(state.Options.RatingKFactor);
        Text(writer, state.Profile.PlayerId); Text(writer, state.Profile.DisplayName); NullableText(writer, state.Profile.PortraitId);
        writer.Write(state.Matches.Count);
        foreach (var match in state.Matches)
        {
            Text(writer, match.MatchId); Text(writer, match.OpponentId); writer.Write(match.OpponentRating);
            writer.Write((byte)match.PlayerSide); writer.Write((int)match.Result.Reason); writer.Write((byte)(match.Result.Winner ?? '\0'));
            writer.Write(match.CompletedAt.UtcDateTime.Ticks); writer.Write((short)match.CompletedAt.Offset.TotalMinutes);
            writer.Write((byte)match.Source); NullableText(writer, match.ReplayReference);
        }
        writer.Write(state.Lessons.Count);
        foreach (var lesson in state.Lessons)
        {
            Text(writer, lesson.LessonId); Text(writer, lesson.Fingerprint); writer.Write(lesson.AttemptId.ToByteArray()); writer.Write(lesson.MistakeCount);
        }
        writer.Write(state.CommerceFacts.Count);
        foreach (var fact in state.CommerceFacts)
        {
            writer.Write((byte)(fact.IsClaim ? 0 : 1));
            if (fact.IsClaim) writer.Write(fact.Quest.Value); else Text(writer, fact.CosmeticId!);
        }
        Text(writer, state.EquippedBoardId); Text(writer, state.EquippedPiecesId);
        // Balances share the same atomic payload as claim/purchase identities and are independently checked on restore.
        var balances = state.Balances();
        writer.Write(balances.Count);
        foreach (var reward in balances) { writer.Write(reward.Key.Value); writer.Write(reward.Value); }
        writer.Flush();
        return stream.ToArray();
    }

    internal static ProgressState Decode(ReadOnlyMemory<byte> payload, string expectedPlayer, ChessProgressCatalog catalog, ChessProgressOptions options)
    {
        if (payload.Length > options.MaximumSaveBytes) throw new InvalidDataException("The profile exceeds its configured payload limit.");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            if (reader.ReadUInt32() != Magic || Text(reader) != catalog.Fingerprint
                || reader.ReadDouble() != options.InitialTrainingRating || reader.ReadDouble() != options.RatingKFactor)
                throw new InvalidDataException("The local profile requires a matching schema, catalog and rating policy; explicit migration is required.");
            string player = Text(reader), name = Text(reader); string? portrait = NullableText(reader);
            if (player != expectedPlayer) throw new InvalidDataException("The save belongs to another player identity.");
            ProgressText.Require(player, nameof(player)); ProgressText.Require(name, nameof(name));
            if (portrait is not null) ProgressText.Require(portrait, nameof(portrait));
            var state = new ProgressState(new PlayerProfile(player, name, portrait), catalog, options);
            int matches = Count(reader, options.HistoryCapacity);
            for (int index = 0; index < matches; index++)
            {
                string id = Text(reader), opponent = Text(reader); double rating = reader.ReadDouble();
                char side = (char)reader.ReadByte(); var reason = (PlayableMatchResultReason)reader.ReadInt32();
                byte winner = reader.ReadByte(); long utcTicks = reader.ReadInt64(); short offset = reader.ReadInt16();
                var source = (ChessMatchProgressSource)reader.ReadByte(); string? replay = NullableText(reader);
                var time = new DateTimeOffset(utcTicks, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(offset));
                state.ApplyMatch(new ChessTrainingMatch(id, opponent, rating, side, new PlayableMatchResult(reason, winner == 0 ? null : (char)winner), time, source, replay));
            }
            int lessons = Count(reader, catalog.Campaign.Stages.Count);
            for (int index = 0; index < lessons; index++)
            {
                string id = Text(reader), fingerprint = Text(reader); byte[] attempt = reader.ReadBytes(16);
                if (attempt.Length != 16) throw new InvalidDataException("The lesson identity was truncated.");
                state.ApplyLesson(new LessonReceipt(id, fingerprint, new Guid(attempt), reader.ReadInt32()));
            }
            int facts = Count(reader, checked(catalog.QuestCatalog.Count + catalog.Cosmetics.Definitions.Count));
            for (int index = 0; index < facts; index++)
            {
                byte kind = reader.ReadByte();
                bool applied = kind switch
                {
                    0 => state.ApplyClaim(new ProgressionId(reader.ReadUInt32())),
                    1 => state.ApplyPurchase(Text(reader)),
                    _ => throw new InvalidDataException("Unknown saved commerce operation.")
                };
                if (!applied) throw new InvalidDataException("The saved commerce operation is duplicated or was not earned.");
            }
            state.RestoreEquipment(Text(reader), Text(reader));
            var expectedBalances = state.Balances();
            int balances = Count(reader, expectedBalances.Count);
            if (balances != expectedBalances.Count) throw new InvalidDataException("Claim/purchase state and reward balances differ.");
            var seen = new HashSet<ProgressionId>();
            for (int index = 0; index < balances; index++)
            {
                var id = new ProgressionId(reader.ReadUInt32()); long amount = reader.ReadInt64();
                if (!seen.Add(id) || !expectedBalances.TryGetValue(id, out long expected) || expected != amount)
                    throw new InvalidDataException("Claim/purchase state and reward balances differ.");
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Bytes follow the local profile payload.");
            return state;
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException("The local profile payload is invalid.", exception);
        }
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException("The profile contains an invalid or excessive collection length.");
        return count;
    }
    private static void Text(BinaryWriter writer, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > 4096) throw new InvalidDataException("The profile string exceeds its limit.");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string Text(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 4096 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("The profile string is truncated or exceeds its limit.");
        return Utf8.GetString(reader.ReadBytes(length));
    }
    private static void NullableText(BinaryWriter writer, string? value) { writer.Write(value is not null); if (value is not null) Text(writer, value); }
    private static string? NullableText(BinaryReader reader) => reader.ReadBoolean() ? Text(reader) : null;
}
