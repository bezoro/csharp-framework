using System.IO;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

/// <summary>Reads decoded chunks, limiting each line before growing its accumulator.</summary>
internal sealed class BoundedLineReader(StreamReader reader, int limit)
{
	// A text reader can fill a requested character count across multiple pipe reads, even
	// after receiving a newline. Decode one available byte chunk at a time instead.
	private readonly byte[] _bytes = new byte[4096];
	private readonly char[] _buffer = new char[reader.CurrentEncoding.GetMaxCharCount(4096)];
	private readonly Decoder _decoder = reader.CurrentEncoding.GetDecoder();
	private bool _atStart = true;
	private bool _eof;
	private int _start;
	private int _end;
	private StringBuilder? _partial;

	public async ValueTask<string?> ReadAsync()
	{
		try
		{
			string? line;
			while (!TryTakeLine(out line))
			{
				if (_eof) return FinishPartial();
				int count = await reader.BaseStream.ReadAsync(_bytes, 0, _bytes.Length, CancellationToken.None).ConfigureAwait(false);
				Decode(count);
			}
			return line;
		}
		catch (ObjectDisposedException) { return null; }
	}

	public string? Read()
	{
		try
		{
			string? line;
			while (!TryTakeLine(out line))
			{
				if (_eof) return FinishPartial();
				Decode(reader.BaseStream.Read(_bytes, 0, _bytes.Length));
			}
			return line;
		}
		catch (ObjectDisposedException) { return null; }
	}

	private void Decode(int count)
	{
		_eof = count == 0;
		_start = 0;
		_end = _decoder.GetChars(_bytes, 0, count, _buffer, 0, flush: _eof);
		if (_atStart && _end != 0)
		{
			_atStart = false;
			if (_buffer[0] == '\uFEFF') _start++;
		}
	}

	private bool TryTakeLine(out string? line)
	{
		line = null;
		if (_start == _end) return false;
		int offset = _start;
		while (_start < _end && _buffer[_start] is not ('\r' or '\n')) _start++;
		int length = _start - offset;
		if (length > limit - (_partial?.Length ?? 0))
			throw new InvalidDataException($"Engine output line exceeds {limit} characters.");
		if (_start < _end && _partial is null)
		{
			line = new string(_buffer, offset, length);
			_start++;
			return true;
		}
		if (length != 0) (_partial ??= new StringBuilder()).Append(_buffer, offset, length);
		if (_start == _end) return false;
		_start++;
		line = FinishPartial();
		return true;
	}

	private string? FinishPartial()
	{
		string? line = _partial?.ToString();
		_partial = null;
		return line;
	}
}
