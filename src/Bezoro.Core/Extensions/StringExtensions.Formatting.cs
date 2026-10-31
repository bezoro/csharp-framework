using System.Text;
using Bezoro.Core.Types;

namespace Bezoro.Core.Extensions;

/// <summary>
///     Provides extension methods for formatting strings with various text markup and transformations.
/// </summary>
public static partial class StringExtensions
{
	/// <summary>
	///     Wraps the string in bold tags (&lt;b&gt;...&lt;/b&gt;).
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <returns>The string wrapped in bold tags.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Bold(this string text) =>
		string.IsNullOrWhiteSpace(text)
			? throw new ArgumentNullException(nameof(text))
			: $"<b>{text}</b>";

	/// <summary>
	///     Surrounds the text with a bracket, padding, and (optionally) colorizing the brackets.
	/// </summary>
	/// <param name="text">The text to wrap.</param>
	/// <param name="padding">The number of spaces to pad on each side inside the brackets.</param>
	/// <param name="color">The color to apply to the brackets. If default, no color is applied.</param>
	/// <param name="bracket">The opening bracket character (e.g., '['), the matching closing bracket will be computed.</param>
	/// <returns>The bracketed (and colorized, if specified) text.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Bracketed(this string text, int padding = 0, Color color = default, char bracket = '[')
	{
		if (string.IsNullOrWhiteSpace(text)) throw new ArgumentNullException(nameof(text));

		char   closingBracket = GetClosingBracket(bracket);
		string paddedText     = text.PadLeft(text.Length + padding).PadRight(text.Length + 2 * padding);

		if (color == default) return $"{bracket}{paddedText}{closingBracket}";

		var colorTag      = $"<color=#{color.R:X2}{color.G:X2}{color.B:X2}>";
		var closeColorTag = "</color>";
		return $"{colorTag}{bracket}{closeColorTag}{paddedText}{colorTag}{closingBracket}{closeColorTag}";
	}

	/// <summary>
	///     Capitalizes the first character of the string.
	/// </summary>
	/// <param name="text">The text to capitalize.</param>
	/// <returns>The input string with its first character made uppercase.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Capitalize(this string text)
	{
		if (string.IsNullOrWhiteSpace(text)) throw new ArgumentNullException(nameof(text));

		return char.ToUpper(text[0]) + text[1..];
	}

	/// <summary>
	///     Wraps the string in italic tags (&lt;i&gt;...&lt;/i&gt;).
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <returns>The string wrapped in italic tags.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Italic(this string text) =>
		string.IsNullOrWhiteSpace(text) ? throw new ArgumentNullException(nameof(text)) : $"<i>{text}</i>";

	/// <summary>
	///     Converts all characters in the string to lowercase.
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <returns>The input string converted to lowercase.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Lowercase(this string text) =>
		string.IsNullOrWhiteSpace(text) ? throw new ArgumentNullException(nameof(text)) : text.ToLower();

	/// <summary>
	///     Repeats a string a specified number of times.
	/// </summary>
	/// <param name="str">The string to repeat.</param>
	/// <param name="count">The number of times to repeat the string.</param>
	/// <returns>
	///     A new string containing the original string repeated the specified number of times.
	/// </returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="str" /> is null or whitespace.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="count" /> is zero.</exception>
	public static string Repeat(this string str, uint count = 1)
	{
		if (string.IsNullOrWhiteSpace(str)) throw new ArgumentNullException(nameof(str));
		if (count == 0)
			throw new ArgumentOutOfRangeException(
				nameof(count),
				count,
				"Count cannot be zero."
			);

		if (count == 1) return str;

		long length = (long)str.Length * count;
		if (length > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(count), "The repeated string must fit in an Int32.");
		var sb = new StringBuilder((int)length);
		for (var i = 0; i < count; i++) sb.Append(str);

		return sb.ToString();
	}

	/// <summary>
	///     Wraps the string in a size tag (&lt;size=N&gt;...&lt;/size&gt;).
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <param name="size">The font size to apply.</param>
	/// <returns>The string wrapped in a size tag.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Size(this string text, int size) =>
		string.IsNullOrWhiteSpace(text) ? throw new ArgumentNullException(nameof(text)) : $"<size={size}>{text}</size>";

	/// <summary>
	///     Wraps the string in strikethrough tags (&lt;s&gt;...&lt;/s&gt;).
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <returns>The string wrapped in strikethrough tags.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Strikethrough(this string text) =>
		string.IsNullOrWhiteSpace(text) ? throw new ArgumentNullException(nameof(text)) : $"<s>{text}</s>";

	/// <summary>
	///     Wraps the string in underline tags (&lt;u&gt;...&lt;/u&gt;).
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <returns>The string wrapped in underline tags.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Underline(this string text) =>
		string.IsNullOrWhiteSpace(text) ? throw new ArgumentNullException(nameof(text)) : $"<u>{text}</u>";

	/// <summary>
	///     Converts all characters in the string to uppercase.
	/// </summary>
	/// <param name="text">The text to format.</param>
	/// <returns>The input string converted to uppercase.</returns>
	/// <exception cref="ArgumentNullException">Thrown if <paramref name="text" /> is null or whitespace.</exception>
	public static string Uppercase(this string text) =>
		string.IsNullOrWhiteSpace(text) ? throw new ArgumentNullException(nameof(text)) : text.ToUpper();

	/// <summary>
	///     Determines the appropriate closing bracket for the specified opening bracket character.
	/// </summary>
	/// <param name="openingBracket">The opening bracket character.</param>
	/// <returns>The matching closing bracket character, or the opening bracket itself for symmetric/custom brackets.</returns>
	private static char GetClosingBracket(char openingBracket)
	{
		return openingBracket switch
		{
			'[' => ']',
			'(' => ')',
			'{' => '}',
			'<' => '>',
			_   => openingBracket // For symmetric brackets or custom characters
		};
	}
}
