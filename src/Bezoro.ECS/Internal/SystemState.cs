using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class SystemState(
	ISystem system,
	Stage   stage,
	int[]   readIds,
	int[]   writeIds,
	bool    isExclusive,
	Type[]  beforeSystemTypes,
	Type[]  afterSystemTypes,
	Type[]  systemSetTypes,
	ISystemRunCondition[] runConditions)
{
	private long _lastExecutionEpoch;
	private long _activeExecutionEpoch;
	private CommandStream? _commandStream;

	internal CommandStream? RecordedCommandStream => _commandStream;

	// Epochs belong to this registration and never repeat. Context copies cannot acquire a later stream.
	internal long BeginExecution()
	{
		if (_lastExecutionEpoch == long.MaxValue)
			throw new InvalidOperationException("The system has exhausted its execution identities.");
		long epoch = ++_lastExecutionEpoch;
		Volatile.Write(ref _activeExecutionEpoch, epoch);
		return epoch;
	}

	internal void EndExecution()
	{
		// Serialize scope closure with first access from a nested parallel query callback.
		lock (this)
			Volatile.Write(ref _activeExecutionEpoch, 0);
	}

	internal void ValidateExecution(long epoch)
	{
		if (epoch == 0 || Volatile.Read(ref _activeExecutionEpoch) != epoch)
			throw new ObjectDisposedException(nameof(SystemContext), "The system update context is no longer active.");
	}

	internal CommandStream GetOrCreateCommandStream(World world, long epoch)
	{
		lock (this)
		{
			ValidateExecution(epoch);
			return _commandStream ??= world.CreateCommandStream();
		}
	}

	internal void DisposeCommandStream()
	{
		var stream = _commandStream;
		_commandStream = null;
		stream?.Dispose();
	}

	public Type[] AfterSystemTypes { get; } = afterSystemTypes;

	public Type[] BeforeSystemTypes { get; } = beforeSystemTypes;

	public bool IsExclusive { get; } = isExclusive;

	public int[] ReadIds { get; } = readIds;

	public int[] WriteIds { get; } = writeIds;

	public ISystem System { get; } = system;

	public Stage Stage { get; } = stage;

	public SystemLoopPhase LoopPhase { get; } = system.LoopPhase;

	public double Accumulator { get; set; }

	public ISystemRunCondition[] RunConditions { get; } = runConditions;

	public Type[] SystemSetTypes { get; } = systemSetTypes;
}
