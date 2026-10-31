namespace Bezoro.Logging.Internal;

internal sealed class AsyncContextScope : IDisposable
{
	private static readonly AsyncLocal<AsyncContextScope?> Current = new();

	internal AsyncContextScope(string contextName)
	{
		if (string.IsNullOrWhiteSpace(contextName))
			throw new ArgumentException("Context name must contain a non-whitespace character.", nameof(contextName));
		Name = contextName;
		Parent = Current.Value;
		Depth = (Parent?.Depth ?? 0) + 1;
		Current.Value = this;
	}

	internal int Depth { get; }

	internal string Name { get; }

	internal AsyncContextScope? Parent { get; }

	internal static IReadOnlyList<string>? CurrentHierarchy => Current.Value?.ToHierarchy();

	public void Dispose()
	{
		// Scope objects flow to child ExecutionContexts. Disposal belongs to the current
		// flow, so a flag on the shared object would prevent its parent's later disposal.
		if (ReferenceEquals(Current.Value, this))
		{
			Current.Value = Parent;
			return;
		}

		for (var scope = Current.Value; scope != null; scope = scope.Parent)
			if (ReferenceEquals(scope, this))
				throw new InvalidOperationException("Dispose async contexts in reverse creation order.");
	}

	private string[] ToHierarchy()
	{
		var hierarchy = new string[Depth];

		for (AsyncContextScope? scope = this; scope != null; scope = scope.Parent)
			hierarchy[scope.Depth - 1] = scope.Name;

		return hierarchy;
	}
}
