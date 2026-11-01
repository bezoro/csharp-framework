using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bezoro.Core.Helpers;

namespace Bezoro.Core.Types;

/// <summary>
///     Represents an immutable RGBA color with 8-bit components. Includes conversion utilities,
///     parsing, formatting, alpha blending (including linear sRGB), and common color operations.
/// </summary>
[Serializable]
[StructLayout(LayoutKind.Sequential)]
[DebuggerDisplay("Color(R={R}, G={G}, B={B}, A={A})")]
public readonly record struct Color(byte R, byte G, byte B, byte A)
	: IFormattable
#if NET6_0_OR_GREATER
		,
		ISpanFormattable
#endif
{
	/// <summary>
	///     Constructs a color from floating-point [0,1] values for each channel.
	/// </summary>
	/// <param name="R">Red channel in [0, 1]</param>
	/// <param name="G">Green channel in [0, 1]</param>
	/// <param name="B">Blue channel in [0, 1]</param>
	/// <param name="A">Alpha channel in [0, 1]</param>
	public Color(float R, float G, float B, float A) : this()
	{
		this.R = FloatToByte(R);
		this.G = FloatToByte(G);
		this.B = FloatToByte(B);
		this.A = FloatToByte(A);
	}

	/// <summary>
	///     White opaque color (default).
	/// </summary>
	public Color() : this(255, 255, 255, 255) { }

	/// <summary>Creates a color from 8-bit RGB, alpha=255</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

	/// <summary>Creates a color from float RGB, alpha=1.0</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromRgb(float r, float g, float b) => new(r, g, b, 1f);

	/// <summary>Creates a color from 8-bit ARGB (common .NET order)</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);

	/// <summary>Creates a color from float ARGB (Alpha in [0,1])</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromArgb(float a, float r, float g, float b) => new(r, g, b, a);

	/// <summary>Transparent black (all channels 0)</summary>
	public static Color Transparent => new(0, 0, 0, 0);

	/// <summary>Opaque white</summary>
	public static Color White => new(255, 255, 255, 255);

	/// <summary>Opaque 50% gray</summary>
	public static Color Gray => new(128, 128, 128, 255);

	/// <summary>Opaque black</summary>
	public static Color Black => new(0, 0, 0, 255);

	/// <summary>Alpha as normalized float [0,1]</summary>
	public float Af
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => A / 255f;
	}

	/// <summary>Blue as normalized float [0,1]</summary>
	public float Bf
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => B / 255f;
	}

	/// <summary>Green as normalized float [0,1]</summary>
	public float Gf
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => G / 255f;
	}

	/// <summary>Red as normalized float [0,1]</summary>
	public float Rf
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => R / 255f;
	}

	/// <summary>Returns a new color with a replaced red component.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Color WithR(byte r) => new(r, G, B, A);

	/// <summary>Returns a new color with a replaced green component.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Color WithG(byte g) => new(R, g, B, A);

	/// <summary>Returns a new color with a replaced blue component.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Color WithB(byte b) => new(R, G, b, A);

	/// <summary>Returns a new color with a replaced alpha component.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Color WithA(byte a) => new(R, G, B, a);

	/// <summary>Returns a new color with an alpha component from a [0,1] float.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Color WithA(float a) => new(Rf, Gf, Bf, a);

	/// <summary>Implicit conversion to System.Drawing.Color.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator System.Drawing.Color(Color color) =>
		System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

	/// <summary>Implicit cast to Vector4 (RGBA in [0,1])</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator Vector4(Color c) => new(c.Rf, c.Gf, c.Bf, c.Af);

	/// <summary>Explicit cast from Vector4 (X=R, Y=G, Z=B, W=A, all in [0,1])</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static explicit operator Color(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

	// --- Packed 32-bit helpers ---

	/// <summary>
	///     Packs color to 0xRRGGBBAA (big-endian).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint ToRgba32() => (uint)R << 24 | (uint)G << 16 | (uint)B << 8 | A;

	/// <summary>
	///     Unpacks color from 0xRRGGBBAA (big-endian).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromRgba32(uint rgba) =>
		unchecked(new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba));

	/// <summary>
	///     Packs color to 0xAARRGGBB (used by .NET System.Drawing).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint ToArgb32() => (uint)A << 24 | (uint)R << 16 | (uint)G << 8 | B;

	/// <summary>
	///     Unpacks color from 0xAARRGGBB.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromArgb32(uint argb) =>
		unchecked(new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24)));

	/// <summary>
	///     Packs color to 0xAABBGGRR (little-endian; memory BGRA).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint ToAbgr32() => (uint)A << 24 | (uint)B << 16 | (uint)G << 8 | R;

	/// <summary>
	///     Unpacks color from 0xAABBGGRR (BGRA).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Color FromAbgr32(uint abgr) =>
		unchecked(new((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16), (byte)(abgr >> 24)));

	/// <summary>
	///     Calculates approximate relative luminance using sRGB coefficients.
	///     Note: best used with linearized color channels.
	/// </summary>
	public float Luminance
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => 0.2126f * ToLinear(Rf) + 0.7152f * ToLinear(Gf) + 0.0722f * ToLinear(Bf);
	}

	/// <summary>
	///     Composites <paramref name="src" /> over <paramref name="dst" /> using sRGB alpha blending.
	/// </summary>
	/// <param name="dst">Destination color under the source.</param>
	/// <param name="src">Source color to blend on top.</param>
	/// <returns>Blended color.</returns>
	public static Color AlphaBlend(Color dst, Color src)
	{
		float sa   = src.Af, da = dst.Af;
		float outA = sa + da * (1f - sa);
		if (FloatComparer.IsZero(outA)) return Transparent;

		float r = (src.Rf * sa + dst.Rf * da * (1f - sa)) / outA;
		float g = (src.Gf * sa + dst.Gf * da * (1f - sa)) / outA;
		float b = (src.Bf * sa + dst.Bf * da * (1f - sa)) / outA;
		return new(r, g, b, outA);
	}

	/// <summary>
	///     Composites <paramref name="src" /> over <paramref name="dst" /> using linear light color space.
	///     Converts to linear, blends, and converts back to sRGB.
	/// </summary>
	/// <param name="dst">Destination color under the source.</param>
	/// <param name="src">Source color to blend on top.</param>
	/// <returns>Blended color.</returns>
	public static Color AlphaBlendLinear(Color dst, Color src)
	{
		// Convert to linear
		float sR = ToLinear(src.Rf), sG = ToLinear(src.Gf), sB = ToLinear(src.Bf), sa = src.Af;
		float dR = ToLinear(dst.Rf), dG = ToLinear(dst.Gf), dB = ToLinear(dst.Bf), da = dst.Af;

		float outA = sa + da * (1f - sa);
		if (FloatComparer.IsZero(outA)) return Transparent;

		float r = (sR * sa + dR * da * (1f - sa)) / outA;
		float g = (sG * sa + dG * da * (1f - sa)) / outA;
		float b = (sB * sa + dB * da * (1f - sa)) / outA;

		// Back to sRGB
		return new(FromLinear(r), FromLinear(g), FromLinear(b), outA);
	}

	/// <summary>
	///     Returns color as #RRGGBB string.
	/// </summary>
	public override string ToString() => ToString("RGB", CultureInfo.InvariantCulture);

	/// <summary>
	///     Formats color using format:
	///     <list type="bullet">
	///         <item>"RGB" – "#RRGGBB"</item>
	///         <item>"RGBA" – "#RRGGBBAA"</item>
	///         <item>"ARGB" – "#AARRGGBB"</item>
	///         <item>"CSS" – "rgba(r, g, b, a)"</item>
	///     </list>
	/// </summary>
	public string ToString(string? format, IFormatProvider? formatProvider)
	{
		Span<char> buffer = stackalloc char[32];
		if (TryFormat(buffer, out int written, format, formatProvider)) return new(buffer[..written]);

		// Fallback
		return $"#{R:X2}{G:X2}{B:X2}";
	}

	/// <summary>
	///     Formats color into a provided span using the given format.
	///     Supported: "RGB", "RGBA", "ARGB", "CSS".
	/// </summary>
	/// <param name="destination">Output buffer.</param>
	/// <param name="charsWritten">Characters written.</param>
	/// <param name="format">Format code (see ToString docs).</param>
	/// <param name="provider">Format provider (unused).</param>
	/// <returns>True if written, false if buffer too small.</returns>
	public bool TryFormat(
		Span<char>         destination,
		out int            charsWritten,
		ReadOnlySpan<char> format,
		IFormatProvider?   provider)
	{
		int kind = format.Equals("RGBA".AsSpan(), StringComparison.OrdinalIgnoreCase) ? 1
			: format.Equals("ARGB".AsSpan(), StringComparison.OrdinalIgnoreCase) ? 2
			: format.Equals("CSS".AsSpan(), StringComparison.OrdinalIgnoreCase) ? 3 : 0;
		switch (kind)
		{
			case 0:
			{
				if (destination.Length < 7)
				{
					charsWritten = 0;
					return false;
				}

				destination[0] = '#';
				WriteHex2(R, destination.Slice(1, 2));
				WriteHex2(G, destination.Slice(3, 2));
				WriteHex2(B, destination.Slice(5, 2));
				charsWritten = 7;
				return true;
			}
			case 1:
			{
				if (destination.Length < 9)
				{
					charsWritten = 0;
					return false;
				}

				destination[0] = '#';
				WriteHex2(R, destination.Slice(1, 2));
				WriteHex2(G, destination.Slice(3, 2));
				WriteHex2(B, destination.Slice(5, 2));
				WriteHex2(A, destination.Slice(7, 2));
				charsWritten = 9;
				return true;
			}
			case 2:
			{
				if (destination.Length < 9)
				{
					charsWritten = 0;
					return false;
				}

				destination[0] = '#';
				WriteHex2(A, destination.Slice(1, 2));
				WriteHex2(R, destination.Slice(3, 2));
				WriteHex2(G, destination.Slice(5, 2));
				WriteHex2(B, destination.Slice(7, 2));
				charsWritten = 9;
				return true;
			}
			case 3:
			{
				// rgba(r, g, b, a) with a in [0,1]
				Span<char> css = stackalloc char[32];
				"rgba(".AsSpan().CopyTo(css);
				int position = 5;
				R.TryFormat(css.Slice(position), out int length, default, CultureInfo.InvariantCulture);
				position += length;
				", ".AsSpan().CopyTo(css.Slice(position));
				position += 2;
				G.TryFormat(css.Slice(position), out length, default, CultureInfo.InvariantCulture);
				position += length;
				", ".AsSpan().CopyTo(css.Slice(position));
				position += 2;
				B.TryFormat(css.Slice(position), out length, default, CultureInfo.InvariantCulture);
				position += length;
				", ".AsSpan().CopyTo(css.Slice(position));
				position += 2;
				Af.TryFormat(css.Slice(position), out length, "0.###", CultureInfo.InvariantCulture);
				position += length;
				css[position++] = ')';

				if (css.Slice(0, position).TryCopyTo(destination))
				{
					charsWritten = position;
					return true;
				}

				charsWritten = 0;
				return false;
			}
			default:
				throw new InvalidOperationException();
		}
	}

	/// <summary>
	///     Attempts to parse a color from common hex and CSS formats.
	///     Accepts "#RRGGBB", "#RRGGBBAA", "#RGB", "#RGBA", "rgba(r, g, b, a)" (CSS),
	///     or without the leading #. Parsing is case-insensitive.
	/// </summary>
	/// <param name="s">Input span</param>
	/// <param name="provider">Format provider for parsing (not used)</param>
	/// <param name="result">Resulting Color</param>
	/// <returns>True if parse successful</returns>
	public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Color result)
	{
		s = s.Trim();

		if (s.Length == 0)
		{
			result = default;
			return false;
		}

		bool hasHexPrefix = s[0] == '#';
		if (hasHexPrefix) s = s[1..];

		// Short forms
		if (s.Length == 3 || s.Length == 4)
		{
			if (TryHexNibble(s[0], out byte r) &&
				TryHexNibble(s[1], out byte g) &&
				TryHexNibble(s[2], out byte b))
			{
				byte a = 0xF;

				if (s.Length == 4 && !TryHexNibble(s[3], out a))
				{
					result = default;
					return false;
				}

				// Expand 4-bit to 8-bit
				var r8 = (byte)(r << 4 | r);
				var g8 = (byte)(g << 4 | g);
				var b8 = (byte)(b << 4 | b);
				var a8 = (byte)(a << 4 | a);
				result = new(r8, g8, b8, s.Length == 4 ? a8 : (byte)255);
				return true;
			}

			result = default;
			return false;
		}

		// Long forms
		if (s.Length == 6 || s.Length == 8)
		{
			if (TryHexByte(s.Slice(0, 2), out byte r) &&
				TryHexByte(s.Slice(2, 2), out byte g) &&
				TryHexByte(s.Slice(4, 2), out byte b))
			{
				byte a = 255;

				if (s.Length == 8 &&
					!TryHexByte(s.Slice(6, 2), out a))
				{
					result = default;
					return false;
				}

				result = new(r, g, b, a);
				return true;
			}

			result = default;
			return false;
		}

		// CSS rgb/rgba require an exact function name and component count.
		int open = s.IndexOf('(');
		bool rgba = open == 4 && s.Slice(0, open).Equals("rgba".AsSpan(), StringComparison.OrdinalIgnoreCase);
		bool rgb = open == 3 && s.Slice(0, open).Equals("rgb".AsSpan(), StringComparison.OrdinalIgnoreCase);
		if (!hasHexPrefix && (rgb || rgba) && s[s.Length - 1] == ')')
		{
			var inner = s.Slice(open + 1, s.Length - open - 2);
			Span<Range> parts = stackalloc Range[4];
			int count = 0;
			int start = 0;
			for (int index = 0; index <= inner.Length; index++)
			{
				if (index != inner.Length && inner[index] != ',') continue;
				if (count == parts.Length) { result = default; return false; }
				parts[count++] = new(start, index);
				start = index + 1;
			}

			if (count == (rgba ? 4 : 3) &&
				int.TryParse(inner[parts[0]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) &&
				int.TryParse(inner[parts[1]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) &&
				int.TryParse(inner[parts[2]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
			{
				float alpha = 1;
				if (rgba && (!float.TryParse(inner[parts[3]].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out alpha) ||
					float.IsNaN(alpha) || float.IsInfinity(alpha)))
				{
					result = default;
					return false;
				}
				result = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), FloatToByte(alpha));
				return true;
			}
		}

		result = default;
		return false;
	}

	/// <summary>Deconstruct as (byte r, byte g, byte b, byte a)</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Deconstruct(out byte r, out byte g, out byte b, out byte a)
	{
		r = R;
		g = G;
		b = B;
		a = A;
	}

	/// <summary>Deconstruct as (float r, float g, float b, float a) in [0,1]</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Deconstruct(out float r, out float g, out float b, out float a)
	{
		r = Rf;
		g = Gf;
		b = Bf;
		a = Af;
	}

	/// <summary>Converts float in [0,1] to 8-bit. Clamps and rounds away from zero.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static byte FloatToByte(float x)
	{
		if (float.IsNaN(x) || float.IsInfinity(x)) throw new ArgumentOutOfRangeException(nameof(x), "Color channels must be finite.");
		// Clamp and round away from zero to reduce rounding bias
		float v = Math.Clamp(x, 0f, 1f) * 255f;
		return (byte)MathF.Round(v, MidpointRounding.AwayFromZero);
	}

	/// <summary>Writes a byte value as two HEX digits into a character Span.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void WriteHex2(byte value, Span<char> dest)
	{
		const string HEX = "0123456789ABCDEF";
		dest[0] = HEX[value >> 4 & 0xF];
		dest[1] = HEX[value & 0xF];
	}

	/// <summary>Tries to parse a single hex nibble from character.</summary>
	private static bool TryHexByte(ReadOnlySpan<char> text, out byte value)
	{
		if (TryHexNibble(text[0], out byte high) && TryHexNibble(text[1], out byte low))
		{
			value = (byte)((high << 4) | low);
			return true;
		}
		value = 0;
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool TryHexNibble(char c, out byte nibble)
	{
		if (c >= '0' && c <= '9')
		{
			nibble = (byte)(c - '0');
			return true;
		}

		if (c >= 'a' && c <= 'f')
		{
			nibble = (byte)(10 + (c - 'a'));
			return true;
		}

		if (c >= 'A' && c <= 'F')
		{
			nibble = (byte)(10 + (c - 'A'));
			return true;
		}

		nibble = 0;
		return false;
	}

	/// <summary>
	///     Converts sRGB [0,1] to linear [0,1] for more perceptually accurate blending.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float ToLinear(float srgb)
		=> srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);

	/// <summary>
	///     Converts linear RGB [0,1] to sRGB [0,1].
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float FromLinear(float linear)
		=> linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
}
