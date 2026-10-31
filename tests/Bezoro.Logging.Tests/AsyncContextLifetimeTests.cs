using Bezoro.Logging.Types;

namespace Bezoro.Logging.Tests;

public sealed class AsyncContextLifetimeTests
{
	[Fact]
	public async Task DisposingInheritedScopeInChild_DoesNotPreventParentDisposal()
	{
		using var settings = new LoggerSettingsScope();
		var scope = LoggerSettings.BeginAsyncContext("root");
		await Task.Run(scope.Dispose);
		scope.Dispose();
		Assert.Null(LoggerSettings.CurrentAsyncContextHierarchy);
	}

	[Fact]
	public void OutOfOrderDisposal_RejectsWithoutChangingCurrentContext()
	{
		using var settings = new LoggerSettingsScope();
		using var parent = LoggerSettings.BeginAsyncContext("parent");
		using var child = LoggerSettings.BeginAsyncContext("child");
		Assert.Throws<InvalidOperationException>(parent.Dispose);
		Assert.Equal(new[] { "parent", "child" }, LoggerSettings.CurrentAsyncContextHierarchy);
	}
}
