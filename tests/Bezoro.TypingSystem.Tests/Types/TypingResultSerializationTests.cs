using System.Reflection;
using System.Text.Json;
using Bezoro.TypingSystem.Types;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.TypingSystem.Tests.Types;

[TestSubject(typeof(TypingResult))]
public class TypingResultSerializationTests
{
	[Fact]
	public void PublicProperties_WhenInspected_ShouldPreserveNamesAndTypes()
	{
		var properties = typeof(TypingResult)
			.GetProperties(BindingFlags.Instance | BindingFlags.Public)
			.ToDictionary(property => property.Name, property => property.PropertyType);

		properties.Should().BeEquivalentTo(new Dictionary<string, Type>
		{
			[nameof(TypingResult.Expected)] = typeof(char),
			[nameof(TypingResult.Input)] = typeof(char),
			[nameof(TypingResult.IsComplete)] = typeof(bool),
			[nameof(TypingResult.IsCorrect)] = typeof(bool),
			[nameof(TypingResult.IsFaulted)] = typeof(bool),
			[nameof(TypingResult.NextPosition)] = typeof(int),
			[nameof(TypingResult.Position)] = typeof(int),
			[nameof(TypingResult.Status)] = typeof(TypingValidationStatus),
			[nameof(TypingResult.TargetLength)] = typeof(int)
		});
	}

	[Fact]
	public void JsonSerialization_WhenUsingFactory_ShouldExposeConsistentStatusAndValues()
	{
		var original = TypingResult.Match('a', 0, 'a', 3);
		string json = JsonSerializer.Serialize(original);

		using var document = JsonDocument.Parse(json);
		document.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
			"IsComplete", "IsCorrect", "IsFaulted", "Expected", "Input", "NextPosition", "Position", "TargetLength", "Status");
		document.RootElement.GetProperty("Status").GetInt32().Should().Be((int)original.Status);
		document.RootElement.GetProperty("Expected").GetString().Should().Be(original.Expected.ToString());
		document.RootElement.GetProperty("Input").GetString().Should().Be(original.Input.ToString());
		document.RootElement.GetProperty("Position").GetInt32().Should().Be(original.Position);
		document.RootElement.GetProperty("NextPosition").GetInt32().Should().Be(original.NextPosition);
		document.RootElement.GetProperty("TargetLength").GetInt32().Should().Be(original.TargetLength);
		document.RootElement.GetProperty("IsCorrect").GetBoolean().Should().Be(original.IsCorrect);
		document.RootElement.GetProperty("IsComplete").GetBoolean().Should().Be(original.IsComplete);
	}

}
