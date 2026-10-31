using Bezoro.Simulation.Attributes;
using Xunit;

namespace Bezoro.Simulation.Tests.Attributes;

public sealed class StatCalculatorTests
{
    private static readonly StatId Damage = new(1);

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Modifier_RejectsNegativeOperationsBeforeAnyCheckedConversion(int operation)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatModifier((StatModifierOperation)operation, 0));
    }

    [Fact]
    public void Modifier_KeepsItsValueAndTwoIntegerFieldsWithinSixteenBytes()
    {
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<StatModifier>());
    }

    [Fact]
    public void Id_DefaultIsInvalidAndPositiveIdsHaveValueEquality()
    {
        Assert.False(default(StatId).IsValid);
        Assert.Equal(new StatId(3), new StatId(3));
        Assert.True(new StatId(3) == new StatId(3));
        Assert.True(new StatId(3) != new StatId(4));
        Assert.Equal("3", new StatId(3).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatId(-1));
    }

    [Fact]
    public void Evaluate_UsesFlatThenAdditivePercentageThenMultiplicativeFactors()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.Multiply, 2),
            new(StatModifierOperation.Add, 20),
            new(StatModifierOperation.AddPercent, 0.10),
            new(StatModifierOperation.Add, 5),
            new(StatModifierOperation.Multiply, 0.5),
            new(StatModifierOperation.AddPercent, 0.15)
        ];

        var result = StatCalculator.Evaluate(new(Damage, 100), modifiers);

        Assert.Equal(156.25, result.Value);
        Assert.Equal(result.Value, result.UnclampedValue);
        Assert.False(result.WasClamped);
        Assert.False(result.WasSaturated);
        Assert.Equal(2, modifiers[0].Value);
    }

    [Fact]
    public void Evaluate_EmptyAndDefaultModifiersLeaveBaseValueIntact()
    {
        var definition = new StatDefinition(Damage, 12);
        Assert.Equal(12, StatCalculator.Evaluate(definition, []).Value);
        Assert.Equal(12, StatCalculator.Evaluate(definition, new StatModifier[3]).Value);
        Assert.Throws<ArgumentException>(() => StatCalculator.Evaluate(default, []));
    }

    [Fact]
    public void Evaluate_HighestPriorityOverrideWinsWithLastInputBreakingTies()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.Override, 20, 5),
            new(StatModifierOperation.Add, 1000),
            new(StatModifierOperation.Override, 40, -1),
            new(StatModifierOperation.Override, 30, 5),
            new(StatModifierOperation.Multiply, 0)
        ];

        Assert.Equal(30, StatCalculator.Evaluate(new(Damage, 100), modifiers).Value);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(50, 50)]
    [InlineData(200, 100)]
    public void Evaluate_ClampsBaseAndOverridesAfterFormula(double raw, double expected)
    {
        var definition = new StatDefinition(Damage, raw, 0, 100);
        var baseResult = StatCalculator.Evaluate(definition, []);
        var overrideResult = StatCalculator.Evaluate(new(Damage, 10, 0, 100),
            new StatModifier[] { new(StatModifierOperation.Override, raw) });

        Assert.Equal(expected, baseResult.Value);
        Assert.Equal(raw, baseResult.UnclampedValue);
        Assert.Equal(raw != expected, baseResult.WasClamped);
        Assert.Equal(expected, overrideResult.Value);
        Assert.Equal(raw != expected, overrideResult.WasClamped);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void DefinitionsAndModifiers_RejectNonFiniteInputs(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatDefinition(Damage, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatDefinition(Damage, 1, value, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatDefinition(Damage, 1, 0, value));
        foreach (var operation in Enum.GetValues<StatModifierOperation>())
            Assert.Throws<ArgumentOutOfRangeException>(() => new StatModifier(operation, value));
    }

    [Fact]
    public void Constructors_RejectInvalidIdentifiersBoundsAndOperations()
    {
        Assert.Throws<ArgumentException>(() => new StatDefinition(default, 1));
        Assert.Throws<ArgumentException>(() => new StatDefinition(Damage, 1, 10, -10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatModifier((StatModifierOperation)(-1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatModifier((StatModifierOperation)4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatModifier(StatModifierOperation.Multiply, -0.1));
    }

    [Fact]
    public void Evaluate_AllowsNegativeCombinedPercentageAndAppliesBoundsLast()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.AddPercent, -0.75),
            new(StatModifierOperation.AddPercent, -0.75)
        ];

        Assert.Equal(-50, StatCalculator.Evaluate(new(Damage, 100), modifiers).Value);
        Assert.Equal(0, StatCalculator.Evaluate(new(Damage, 100, 0), modifiers).Value);
    }

    [Theory]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(double.MinValue, double.MinValue)]
    public void Evaluate_SaturatesAdditionToFiniteLimit(double baseValue, double addition)
    {
        var result = StatCalculator.Evaluate(new(Damage, baseValue),
            new StatModifier[] { new(StatModifierOperation.Add, addition) });

        Assert.Equal(baseValue, result.Value);
        Assert.True(result.WasSaturated);
        Assert.False(double.IsInfinity(result.Value));
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void Evaluate_OverflowFollowedByZeroFactorNeverProducesNaN()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.Add, double.MaxValue),
            new(StatModifierOperation.Add, double.MaxValue),
            new(StatModifierOperation.Multiply, double.MaxValue),
            new(StatModifierOperation.Multiply, double.MaxValue),
            new(StatModifierOperation.Multiply, 0)
        ];

        var result = StatCalculator.Evaluate(new(Damage, double.MaxValue), modifiers);

        Assert.Equal(0, result.Value);
        Assert.True(result.WasSaturated);
        Assert.False(result.WasClamped);
    }

    [Fact]
    public void Evaluate_ExtremeTermsCancelAccordingToOrderedSaturatingArithmetic()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.Add, double.MaxValue),
            new(StatModifierOperation.Add, double.MaxValue),
            new(StatModifierOperation.Add, double.MinValue)
        ];

        var result = StatCalculator.Evaluate(new(Damage, 12), modifiers);

        Assert.Equal(12, result.Value);
        Assert.True(result.WasSaturated);
        Assert.False(double.IsNaN(result.UnclampedValue));
    }

    [Fact]
    public void Evaluate_ExtremePercentagesAndFactorsStayFinite()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.AddPercent, double.MaxValue),
            new(StatModifierOperation.AddPercent, double.MaxValue),
            new(StatModifierOperation.Multiply, double.MaxValue)
        ];

        var positive = StatCalculator.Evaluate(new(Damage, 2, -100, 100), modifiers);
        var negative = StatCalculator.Evaluate(new(Damage, -2, -100, 100), modifiers);

        Assert.Equal(100, positive.Value);
        Assert.Equal(-100, negative.Value);
        Assert.True(positive.WasSaturated);
        Assert.True(negative.WasClamped);
        Assert.Equal(double.MinValue, negative.UnclampedValue);
    }

    [Fact]
    public void Evaluate_OverrideDoesNotReportOverflowFromDiscardedFormula()
    {
        StatModifier[] modifiers =
        [
            new(StatModifierOperation.Add, double.MaxValue),
            new(StatModifierOperation.Add, double.MaxValue),
            new(StatModifierOperation.Override, 8)
        ];

        var result = StatCalculator.Evaluate(new(Damage, double.MaxValue), modifiers);

        Assert.Equal(8, result.Value);
        Assert.False(result.WasSaturated);
    }

    [Fact]
    public void Evaluate_MatchesIndependentReferenceForFiniteModerateInputs()
    {
        var random = new Random(9017);
        for (int sample = 0; sample < 1000; sample++)
        {
            double baseValue = random.Next(-50, 100);
            var modifiers = new StatModifier[random.Next(0, 20)];
            double flat = 0, percent = 0, factor = 1, replacement = 0;
            int priority = int.MinValue;
            bool hasOverride = false;
            for (int i = 0; i < modifiers.Length; i++)
            {
                var operation = (StatModifierOperation)random.Next(4);
                double value = operation == StatModifierOperation.Multiply ? random.Next(0, 9) / 4.0 : random.Next(-8, 9) / 4.0;
                int order = random.Next(-2, 3);
                modifiers[i] = new(operation, value, order);
                switch (operation)
                {
                    case StatModifierOperation.Add: flat += value; break;
                    case StatModifierOperation.AddPercent: percent += value; break;
                    case StatModifierOperation.Multiply: factor *= value; break;
                    case StatModifierOperation.Override:
                        if (!hasOverride || order >= priority)
                        {
                            hasOverride = true;
                            replacement = value;
                            priority = order;
                        }
                        break;
                }
            }
            double expected = Math.Clamp(hasOverride ? replacement : (baseValue + flat) * (1 + percent) * factor, -1000, 1000);

            Assert.Equal(expected, StatCalculator.Evaluate(new(Damage, baseValue, -1000, 1000), modifiers).Value, 12);
        }
    }
}
