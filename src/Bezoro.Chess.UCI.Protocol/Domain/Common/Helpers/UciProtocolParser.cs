using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Constants;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

/// <summary>
///     Parses raw engine output lines into typed UCI protocol messages.
/// </summary>
internal static class UciProtocolParser
{
	public static bool TryParse(string line, out UciProtocolMessage message)
	{
		message = default;
		if (string.IsNullOrWhiteSpace(line)) return false;

		string trimmed = line.Trim();

		if (UciInfoParser.TryParseTrimmed(trimmed, out var info))
		{
			message = UciProtocolMessage.From(new UciInfoMessage(info, trimmed));
			return true;
		}

		if (trimmed.Equals(UciConstants.Responses.UCI_OK, StringComparison.OrdinalIgnoreCase))
		{
			message = UciProtocolMessage.From(new UciUciOkMessage(trimmed));
			return true;
		}

		if (trimmed.Equals(UciConstants.Responses.READY_OK, StringComparison.OrdinalIgnoreCase))
		{
			message = UciProtocolMessage.From(new UciReadyOkMessage(trimmed));
			return true;
		}

		if (TryParseId(trimmed, out var idMessage))
		{
			message = UciProtocolMessage.From(idMessage);
			return true;
		}

		if (UciEngineOption.TryParse(trimmed, out var option))
		{
			message = UciProtocolMessage.From(new UciOptionMessage(option, trimmed));
			return true;
		}

		if (BestMoveLine.TryParse(trimmed, out var bestMove))
		{
			if ((!UciCommandBuilder.IsUciMoveString(bestMove.BestMove) &&
				 bestMove.BestMove != "0000" && bestMove.BestMove != "(none)") ||
				(bestMove.HasPonder && !UciCommandBuilder.IsUciMoveString(bestMove.PonderMove!)))
			{
				return false;
			}

			message = UciProtocolMessage.From(new UciBestMoveMessage(
				bestMove.BestMove,
				bestMove.PonderMove ?? string.Empty,
				trimmed
			));
			return true;
		}

		if (TryParseProtection(trimmed, "copyprotection", static (state, raw) => new UciCopyProtectionMessage(state, raw), out var copyProtection))
		{
			message = UciProtocolMessage.From(copyProtection);
			return true;
		}

		if (TryParseProtection(trimmed, "registration", static (state, raw) => new UciRegistrationMessage(state, raw), out var registration))
		{
			message = UciProtocolMessage.From(registration);
			return true;
		}

		return false;
	}

	private static bool TryParseId(string line, out UciIdMessage message)
	{
		message = default;

		if (TryParseIdLine(line, UciConstants.Keywords.NAME, UciIdKind.Name, out message))
			return true;

		return TryParseIdLine(line, UciConstants.Keywords.AUTHOR, UciIdKind.Author, out message);
	}

	private static bool TryParseIdLine(
		string         line,
		string         idToken,
		UciIdKind      kind,
		out UciIdMessage message)
	{
		string prefix = $"{UciConstants.Prefixes.ID} {idToken} ";
		if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			message = new(kind, line[prefix.Length..].Trim(), line);
			return true;
		}

		message = default;
		return false;
	}

	private static bool TryParseProtection<TMessage>(
		string                                line,
		string                                keyword,
		Func<UciProtectionState, string, TMessage> factory,
		out TMessage?                         message)
	{
		string prefix = keyword + " ";
		if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			message = default;
			return false;
		}

		string payload = line[prefix.Length..].Trim();
		if (!TryParseProtectionState(payload, out var state))
		{
			message = default;
			return false;
		}

		message = factory(state, line);
		return true;
	}

	private static bool TryParseProtectionState(string value, out UciProtectionState state)
	{
		switch (value.ToLowerInvariant())
		{
			case "checking":
				state = UciProtectionState.Checking;
				return true;
			case "ok":
				state = UciProtectionState.Ok;
				return true;
			case "error":
				state = UciProtectionState.Error;
				return true;
			default:
				state = default;
				return false;
		}
	}

}
