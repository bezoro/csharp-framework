using System;
using System.Numerics;
using System.Reflection;
using Bezoro.Core.Types;
using Xunit;

namespace Bezoro.Core.Tests.Types;

public class CoreBoundaryRegressionTests
{
	[Theory]
	[InlineData(4294967296f)]
	[InlineData(float.MaxValue)]
	public void UIntVectorRejectsValuesAboveUInt32(float value)
	{
		Assert.False(UIntVector2.TryFromVector2(new Vector2(value, 0), out _));
		Assert.Throws<ArgumentOutOfRangeException>(() => UIntVector2.FromVector2(new Vector2(value, 0)));
	}

	[Fact]
	public void UIntVectorAcceptsTheLargestRepresentableFloatBelowUInt32Maximum()
	{
		Assert.Equal(4294967040u, UIntVector2.FromVector2(new Vector2(4294967040f, 0)).X);
	}

	[Fact]
	public void ColorCssFormattingPreservesTheRequestedFormat()
	{
		Assert.Equal("rgba(255, 128, 64, 0.502)", new Color(255, 128, 64, 128).ToString("CSS", null));
	}

	[Theory]
	[InlineData("rgbjunk(1,2,3)")]
	[InlineData("rgb(1,2,3)junk")]
	[InlineData("rgb(1,2,3,0.5)")]
	[InlineData("rgba(1,2,3)")]
	[InlineData("rgba(1,2,3,0.5,9)")]
	[InlineData("rgba(1,2,3,NaN)")]
	[InlineData("rgba(1,2,3,Infinity)")]
	public void ColorParserRejectsMalformedCss(string text)
	{
		Assert.False(Color.TryParse(text, null, out var color));
		Assert.Equal(default, color);
	}

	[Fact]
	public void PackedColorRoundTripsAllLayouts()
	{
		var color = new Color(0x12, 0x34, 0x56, 0x78);
		Assert.Equal(color, Color.FromRgba32(color.ToRgba32()));
		Assert.Equal(color, Color.FromArgb32(color.ToArgb32()));
		Assert.Equal(color, Color.FromAbgr32(color.ToAbgr32()));
	}

	[Fact]
	public void HexColorFormattingDoesNotAllocate()
	{
		var color = Color.White;
		Span<char> destination = stackalloc char[9];
		for (int i = 0; i < 100; i++) color.TryFormat(destination, out _, "rgba", null);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 100; i++) color.TryFormat(destination, out _, "rgba", null);
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[Fact]
	public void SwapbackVersionWrapDoesNotTurnASuccessfulMutationIntoAnOverflowFailure()
	{
		var values = new SwapbackArray<int>();
		typeof(SwapbackArray<int>).GetProperty(nameof(values.Version), BindingFlags.Public | BindingFlags.Instance)!
			.SetValue(values, uint.MaxValue);
		values.Add(42);
		Assert.Equal(0u, values.Version);
		Assert.Equal(42, values[0]);
	}

	[Fact]
	public void CombiningTwoPercentTotalsDoesNotAllocateAnArray()
	{
		for (int i = 0; i < 100; i++) Percent.FromTotals((uint.MaxValue, uint.MaxValue), (0, uint.MaxValue));
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 100; i++) Percent.FromTotals((uint.MaxValue, uint.MaxValue), (0, uint.MaxValue));
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
		Assert.Equal(new Percent(50), Percent.FromTotals((uint.MaxValue, uint.MaxValue), (0, uint.MaxValue)));
	}
}
