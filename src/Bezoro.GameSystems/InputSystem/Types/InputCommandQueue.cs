using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Bezoro.GameSystems.InputSystem.Abstractions;

namespace Bezoro.GameSystems.InputSystem.Types;

/// <summary>
///     Thread-safe ingress queue for external input commands.
/// </summary>
/// <remarks>
///     Producers can enqueue from any thread. ECS systems drain and consume latest-per-control snapshots on the world thread.
/// </remarks>
public sealed class InputCommandQueue : IInputCommandSink
{
	private readonly ConcurrentQueue<InputCommand>      _pendingCommands = new();
	private readonly Dictionary<int, BufferedInputState> _latestByControl = [];

	private double _simulationTimeSeconds;

	/// <summary>
	///     Gets the simulation time accumulated by ingestion updates.
	/// </summary>
	public double SimulationTimeSeconds => _simulationTimeSeconds;

	/// <summary>Approximate pending ingress count. Producers may change it concurrently.</summary>
	public int PendingCommandCount => _pendingCommands.Count;

	/// <summary>Number of retained control snapshots. Read on the world thread.</summary>
	public int TrackedControlCount => _latestByControl.Count;

	/// <summary>Forgets a control's snapshot and sequence on the world thread.</summary>
	/// <remarks>Pending commands remain queued and can recreate the control on a later update.</remarks>
	public bool ForgetControl(int controlId)
	{
		if (controlId < 0) throw new ArgumentOutOfRangeException(nameof(controlId));
		return _latestByControl.Remove(controlId);
	}

	/// <inheritdoc />
	public void Enqueue(in InputCommand command) => _pendingCommands.Enqueue(command);

	/// <summary>
	///     Enqueues a new command for the provided control id.
	/// </summary>
	/// <param name="controlId">Logical control channel identifier.</param>
	/// <param name="moveX">Commanded X movement component.</param>
	/// <param name="moveY">Commanded Y movement component.</param>
	/// <param name="moveZ">Commanded Z movement component.</param>
	/// <param name="sequence">Monotonic sequence number for ordering.</param>
	public void Enqueue(int controlId, float moveX, float moveY, float moveZ, ulong sequence) =>
		Enqueue(new InputCommand(controlId, moveX, moveY, moveZ, sequence));

	internal void AdvanceTime(float deltaTimeSeconds)
	{
		if (deltaTimeSeconds <= 0f || float.IsNaN(deltaTimeSeconds) || float.IsInfinity(deltaTimeSeconds)) return;
		_simulationTimeSeconds += deltaTimeSeconds;
	}

	internal void Drain(int maxCommands)
	{
		for (int i = 0; i < maxCommands && _pendingCommands.TryDequeue(out var command); i++)
		{
			if (_latestByControl.TryGetValue(command.ControlId, out var existing) &&
				command.Sequence <= existing.Sequence)
				continue;

			_latestByControl[command.ControlId] = new(
				command.MoveX,
				command.MoveY,
				command.MoveZ,
				command.Sequence,
				_simulationTimeSeconds
			);
		}
	}

	internal bool TryGetLatest(int controlId, out BufferedInputState state) =>
		_latestByControl.TryGetValue(controlId, out state);
}
