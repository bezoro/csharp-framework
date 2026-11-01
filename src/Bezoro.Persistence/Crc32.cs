namespace Bezoro.Persistence;

/// <summary>Computes the IEEE CRC-32 checksum, using the reflected polynomial <c>0xEDB88320</c>.</summary>
/// <remarks>This detects accidental corruption. It does not authenticate data or provide encryption.</remarks>
public static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>Computes a checksum. The checksum of an empty span is zero.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues a previously finalized checksum with another block.</summary>
    public static uint Append(uint checksum, ReadOnlySpan<byte> data)
    {
        uint state = ~checksum;
        foreach (byte value in data)
            state = Table[unchecked((byte)(state ^ value))] ^ (state >> 8);
        return ~state;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint state = value;
            for (int bit = 0; bit < 8; bit++)
                state = (state & 1) != 0 ? (state >> 1) ^ 0xEDB88320u : state >> 1;
            table[value] = state;
        }
        return table;
    }
}
