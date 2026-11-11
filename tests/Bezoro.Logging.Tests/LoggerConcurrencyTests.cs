using System.Collections.Concurrent;
using Bezoro.Logging.Types;

namespace Bezoro.Logging.Tests;

public sealed class LoggerConcurrencyTests
{
	[Fact]
	public void ConcurrentCategoryMutations_DoNotLoseMembershipOrInvalidateAnEnumerator()
	{
		using var settings = new LoggerSettingsScope();
		var categories = Enum.GetValues<LogCategory>();
		Parallel.ForEach(categories, category => LoggerSettings.MutedCategories.Add(category));
		Assert.True(LoggerSettings.MutedCategories.SetEquals(categories));
		using var snapshot = LoggerSettings.MutedCategories.GetEnumerator();
		LoggerSettings.MutedCategories.Clear();
		var observed = new List<LogCategory>();
		while (snapshot.MoveNext()) observed.Add(snapshot.Current);
		Assert.Equal(categories.Order(), observed.Order());
		Assert.Empty(LoggerSettings.MutedCategories);
	}

	[Fact]
	public void ConcurrentConfigurationPublication_DoesNotTearValueTypeSettings()
	{
		using var settings = new LoggerSettingsScope();
		Func<string?> firstProvider = () => "first";
		Func<string?> secondProvider = () => "second";
		Func<string?, string?, string?> firstDivider = (_, _) => "first divider";
		Func<string?, string?, string?> secondDivider = (_, _) => "second divider";
		var first = StageConfig.Create(firstProvider, firstDivider);
		var second = StageConfig.Create(secondProvider, secondDivider);
		LoggerSettings.Stage = first;
		Parallel.For(0, 4, worker =>
		{
			for (int i = 0; i < 20_000; i++)
			{
				if (worker == 0) LoggerSettings.Stage = i % 2 == 0 ? first : second;
				else
				{
					var captured = LoggerSettings.Stage;
					Assert.True(captured.Enabled);
					Assert.True(ReferenceEquals(captured.Provider, firstProvider)
						? ReferenceEquals(captured.DividerProvider, firstDivider)
						: ReferenceEquals(captured.Provider, secondProvider) && ReferenceEquals(captured.DividerProvider, secondDivider));
				}
			}
		});
	}

	[Fact]
	public void ConcurrentLoggingAndReconfiguration_PreserveAllAdmittedMessages()
	{
		using var settings = new LoggerSettingsScope();
		var messages = new ConcurrentBag<LogPayload>();
		Action<LogPayload> sink = messages.Add;
		Logger.OnLog += sink;
		try
		{
			Parallel.For(0, 4, worker =>
			{
				for (int i = 0; i < 2000; i++)
				{
					if (worker == 0)
					{
						LoggerSettings.Grouping = GroupingConfig.Create(LoggerSettings.ContextGrouping.TimeWindow, timeWindowMs: i + 1);
						LoggerSettings.MutedCategories.Add(LogCategory.Debug);
						LoggerSettings.MutedCategories.Remove(LogCategory.Debug);
					}
					else Logger.Log("parallel", category: LogCategory.Gameplay);
				}
			});
		}
		finally { Logger.OnLog -= sink; }
		Assert.Equal(6000, messages.Count);
		Assert.All(messages, payload => Assert.Equal("parallel", payload.Message));
	}

	[Fact]
	public async Task CategoryBatchInput_IsEnumeratedOutsideTheMutationGate()
	{
		using var settings = new LoggerSettingsScope();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		IEnumerable<LogCategory> Categories()
		{
			entered.Set();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
			yield return LogCategory.Debug;
		}
		var union = Task.Run(() => LoggerSettings.MutedCategories.UnionWith(Categories()));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
			await Task.Run(() => LoggerSettings.MutedCategories.Add(LogCategory.Gameplay)).WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally { release.Set(); await union.WaitAsync(TimeSpan.FromSeconds(10)); }
		Assert.True(LoggerSettings.MutedCategories.SetEquals([LogCategory.Debug, LogCategory.Gameplay]));
	}
}
