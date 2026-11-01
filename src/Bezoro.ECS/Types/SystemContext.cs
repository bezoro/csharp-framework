using Bezoro.ECS.Internal;
using Bezoro.ECS.Services;

namespace Bezoro.ECS.Types;

/// <summary>
///     Provides context for a system update execution.
///     Scheduler-provided contexts are valid only during their system's Update call.
/// </summary>
public readonly struct SystemContext
{
	private readonly CommandStream? _commandStream;
	private readonly SystemState? _executionOwner;
	private readonly long _executionEpoch;
	private readonly float _deltaTime;
	private readonly Stage _stage;
	private readonly World? _world;

	/// <summary>
	///     Initializes a new system update context with the canonical command stream.
	/// </summary>
	/// <param name="deltaTime">The elapsed time passed to this system update.</param>
	/// <param name="stage">The stage executing this context.</param>
	/// <param name="world">The world instance executing this context.</param>
	/// <param name="commandStream">The caller-owned command stream for deferred structural changes.</param>
	/// <exception cref="ArgumentNullException">The world or command stream is null.</exception>
	/// <exception cref="ArgumentException">The command stream belongs to a different world.</exception>
	public SystemContext(float deltaTime, Stage stage, World world, CommandStream commandStream)
	{
		_commandStream = commandStream ?? throw new ArgumentNullException(nameof(commandStream));
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (!ReferenceEquals(commandStream.Owner, world))
			throw new ArgumentException("The command stream belongs to a different world.", nameof(commandStream));
		_deltaTime = deltaTime;
		_stage = stage;
		_world = world;
		_executionOwner = null;
		_executionEpoch = 0;
	}

	internal SystemContext(float deltaTime, Stage stage, World world, SystemState executionOwner, long executionEpoch)
	{
		_deltaTime = deltaTime;
		_stage = stage;
		_world = world;
		_commandStream = null;
		_executionOwner = executionOwner;
		_executionEpoch = executionEpoch;
	}

	/// <summary>
	///     Gets the command stream for deferred structural changes.
	/// </summary>
	/// <remarks>
	///     For scheduler-provided contexts, first access creates the stream. The scheduler plays back
	///     commands after the batch and then disposes the stream. Explicitly constructed contexts use
	///     the supplied caller-owned stream.
	/// </remarks>
	/// <exception cref="ObjectDisposedException">This scheduler-provided context has expired.</exception>
	public CommandStream CommandStream
	{
		get
		{
			if (_executionOwner is { } owner)
				return owner.GetOrCreateCommandStream(_world!, _executionEpoch);
			return _commandStream ?? throw new InvalidOperationException("The system context is uninitialized.");
		}
	}

	/// <summary>
	///     Gets the elapsed time passed to this system update.
	/// </summary>
	public float DeltaTime
	{
		get { ValidateExecution(); return _deltaTime; }
	}

	/// <summary>
	///     Gets the stage currently being executed.
	/// </summary>
	public Stage Stage
	{
		get { ValidateExecution(); return _stage; }
	}

	/// <summary>
	///     Gets the world instance executing this context.
	/// </summary>
	public World World
	{
		get
		{
			ValidateExecution();
			return _world ?? throw new InvalidOperationException("The system context is uninitialized.");
		}
	}

	private void ValidateExecution() => _executionOwner?.ValidateExecution(_executionEpoch);
}
