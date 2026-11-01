using System.Collections.Generic;

using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal sealed class UciPonderRuntime : IAsyncDisposable, IDisposable
{
	private readonly object                          _scoreLock = new();
	private readonly UciEngineClient                 _client;
	private readonly SemaphoreSlim                   _searchOperationGate = new(1, 1);
	private readonly Func<CancellationToken, Task>?  _stopHook;

	private int? _lastScoreCp;
	private int? _lastScoreMate;
	private int  _searchActive;
	private int  _forwardingGeneration;
	private int  _searchGeneration;

	public UciPonderRuntime(UciEngineClient client, Func<CancellationToken, Task>? stopHook = null)
	{
		_client     = client ?? throw new ArgumentNullException(nameof(client));
		_stopHook   = stopHook;
		_client.InfoPvReceived += OnClientInfoPvReceived;
	}

	public event Action<string, string?>?                  BestMove;
	public event Action<PrincipalVariation>?               InfoPv;
	internal event Action<string, string?, int>?           BestMoveWithGeneration;
	internal event Action<PrincipalVariation, int>?        InfoPvWithGeneration;

	public bool IsHealthy => _client.IsHealthy;
	public bool IsStarted => _client.IsStarted;
	internal bool HasActiveSearch => Volatile.Read(ref _searchActive) != 0;
	public int CurrentSearchGeneration => Volatile.Read(ref _searchGeneration);

	public EngineActivity  Activity => _client.Activity;
	public TransportStatus Status   => _client.Status;

	public async Task NewGameAsync(CancellationToken ct = default)
	{
		await StopSearchAsync(ct).ConfigureAwait(false);
		await _client.UciNewGameAsync(ct).ConfigureAwait(false);
		ClearLastScores();
	}

	public Task SetOptionAsync(string name, string? value, CancellationToken ct = default) =>
		_client.SetOptionAsync(name, value, ct);

	public Task SetDebugAsync(bool enabled, CancellationToken ct = default) =>
		_client.SetDebugAsync(enabled, ct);

	public Task RegisterAsync(UciRegistration registration, CancellationToken ct = default) =>
		_client.RegisterAsync(registration, ct);

	public Task SetPositionAsync(Fen fen, IEnumerable<string>? moves = null, CancellationToken ct = default) =>
		_client.SetPositionAsync(fen, moves, ct);

	public async Task StartAsync(CancellationToken ct = default)
	{
		await _client.StartAsync(ct).ConfigureAwait(false);
		ClearLastScores();
	}

	public async Task StartSearchAsync(
		Fen                  fen,
		IEnumerable<string>? playedMoves,
		CancellationToken    ct = default)
	{
		await _searchOperationGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (Volatile.Read(ref _searchActive) == 1)
				await StopSearchCoreAsync(ct).ConfigureAwait(false);

			ClearLastScores();
			DisableOutputForwarding();
			int generation = Interlocked.Increment(ref _searchGeneration);

			try
			{
				await _client.SetPositionAsync(fen, playedMoves, ct).ConfigureAwait(false);
				EnableOutputForwarding(generation);
				await _client.GoFireAndForgetAsync(new() { Infinite = true }, ct).ConfigureAwait(false);
				Volatile.Write(ref _searchActive, 1);
			}
			catch
			{
				DisableOutputForwarding();
				Volatile.Write(ref _searchActive, 0);
				Interlocked.CompareExchange(ref _searchGeneration, generation + 1, generation);
				throw;
			}
		}
		finally
		{
			_searchOperationGate.Release();
		}
	}

	public async Task StopAsync(CancellationToken ct = default)
	{
		await StopSearchAsync(ct).ConfigureAwait(false);
		await _client.StopAsync(ct).ConfigureAwait(false);
		ClearLastScores();
		if (_stopHook is { })
			await _stopHook(ct).ConfigureAwait(false);
	}

	public async Task StopSearchAsync(CancellationToken ct = default)
	{
		await _searchOperationGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			await StopSearchCoreAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			_searchOperationGate.Release();
		}
	}

	private async Task StopSearchCoreAsync(CancellationToken ct)
	{
		DisableOutputForwarding();
		Volatile.Write(ref _searchActive, 0);
		Interlocked.Increment(ref _searchGeneration);

		lock (_scoreLock)
		{
			_lastScoreMate = null;
			_lastScoreCp   = null;
		}

		if (!_client.IsStarted)
			return;

		await _client.StopSearchAsync(ct).ConfigureAwait(false);
		await _client.IsReadyAsync(ct).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		_client.InfoPvReceived -= OnClientInfoPvReceived;
		await _client.DisposeAsync().ConfigureAwait(false);
	}

	public void Dispose()
	{
		DisposeAsync().AsTask().GetAwaiter().GetResult();
	}

	internal void EnableOutputForwardingForTests(int generation = 1)
	{
		Volatile.Write(ref _searchGeneration, generation);
		EnableOutputForwarding(generation);
	}

	internal static bool IsScoreImproved(int? newMate, int? newCp, int? lastMate, int? lastCp)
	{
		if (newMate.HasValue)
		{
			if (lastMate.HasValue)
			{
				if (newMate.Value > 0 && lastMate.Value > 0)
					return newMate.Value < lastMate.Value;

				if (newMate.Value < 0 && lastMate.Value < 0)
					return newMate.Value > lastMate.Value;

				return newMate.Value > lastMate.Value;
			}

			return true;
		}

		if (!newCp.HasValue)
			return false;

		if (lastMate.HasValue)
		{
			if (lastMate.Value > 0)
				return false;

			if (newCp.Value > 0)
				return true;

			return !lastCp.HasValue || newCp.Value > lastCp.Value;
		}

		return !lastCp.HasValue || newCp.Value > lastCp.Value;
	}

	internal void OnClientInfoPvReceived(PrincipalVariation pv)
	{
		int generation = Volatile.Read(ref _forwardingGeneration);
		if (generation == 0)
			return;

		InfoPvWithGeneration?.Invoke(pv, generation);
		InfoPv?.Invoke(pv);

		int? newMate = pv.ScoreMate;
		int? newCp   = pv.ScoreCp;

		bool improved;
		lock (_scoreLock)
		{
			improved = IsScoreImproved(newMate, newCp, _lastScoreMate, _lastScoreCp);
			if (improved)
			{
				_lastScoreMate = newMate;
				_lastScoreCp   = newCp;
			}
		}

		if (!improved)
			return;

		string bestStr = pv.Moves.Length > 0 ? pv.Moves[0] : string.Empty;
		if (string.IsNullOrWhiteSpace(bestStr))
			return;

		string? ponderStr = null;
		if (pv.Moves.Length > 1)
		{
			string candidate = pv.Moves[1];
			if (!string.IsNullOrWhiteSpace(candidate))
				ponderStr = candidate;
		}

		BestMoveWithGeneration?.Invoke(bestStr, ponderStr, generation);
		BestMove?.Invoke(bestStr, ponderStr);
	}

	private void ClearLastScores()
	{
		lock (_scoreLock)
		{
			_lastScoreMate = null;
			_lastScoreCp   = null;
		}
	}

	private void EnableOutputForwarding(int generation) =>
		Volatile.Write(ref _forwardingGeneration, generation);

	private void DisableOutputForwarding() =>
		Volatile.Write(ref _forwardingGeneration, 0);
}
