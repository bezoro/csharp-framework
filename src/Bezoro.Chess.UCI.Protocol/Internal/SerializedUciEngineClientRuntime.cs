using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal sealed class SerializedUciEngineClientRuntime : IAsyncDisposable, IDisposable
{
	private readonly SemaphoreSlim   _gate = new(1, 1);
	private readonly UciEngineClient _client;

	public SerializedUciEngineClientRuntime(UciEngineClient client)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
	}

	public bool IsHealthy => _client.IsHealthy;
	public bool IsStarted => _client.IsStarted;
	public UciEngineInfo EngineInfo => _client.EngineInfo;
	public ImmutableArray<UciEngineOption> AvailableOptions => _client.AvailableOptions;
	public UciEngineCapabilities Capabilities => _client.Capabilities;

	public async Task StartAsync(CancellationToken ct = default) =>
		await ExecuteAsync(client => client.StartAsync(ct), ct).ConfigureAwait(false);

	public async Task StartWithCoordinatorCapabilitiesAsync(CancellationToken ct = default) =>
		await ExecuteAsync(
			async client =>
			{
				await client.StartAsync(ct).ConfigureAwait(false);
				await UciEngineClientCapabilityProbe.ProbeCoordinatorExtensionsAsync(client, ct).ConfigureAwait(false);
			},
			ct
		).ConfigureAwait(false);

	public async Task StopAsync(CancellationToken ct = default) =>
		await ExecuteAsync(client => client.StopAsync(ct), ct).ConfigureAwait(false);

	public async Task NewGameAsync(CancellationToken ct = default) =>
		await ExecuteAsync(client => client.UciNewGameAsync(ct), ct).ConfigureAwait(false);

	public async Task SetOptionAsync(string name, string? value, CancellationToken ct = default) =>
		await ExecuteAsync(client => client.SetOptionAsync(name, value, ct), ct).ConfigureAwait(false);

	public Task SetStrengthLimitAsync(int elo, CancellationToken ct = default) =>
		ExecuteAsync(client => client.SetStrengthLimitAsync(elo, ct), ct);

	public Task ClearStrengthLimitAsync(CancellationToken ct = default) =>
		ExecuteAsync(client => client.ClearStrengthLimitAsync(ct), ct);

	public async Task SetDebugAsync(bool enabled, CancellationToken ct = default) =>
		await ExecuteAsync(client => client.SetDebugAsync(enabled, ct), ct).ConfigureAwait(false);

	public async Task RegisterAsync(UciRegistration registration, CancellationToken ct = default) =>
		await ExecuteAsync(client => client.RegisterAsync(registration, ct), ct).ConfigureAwait(false);

	public async Task SetPositionAsync(Fen fen, IEnumerable<string>? moves, CancellationToken ct = default) =>
		await ExecuteAsync(client => client.SetPositionAsync(fen, moves, ct), ct).ConfigureAwait(false);

	public async Task<Fen?> GetFenAsync(CancellationToken ct = default) =>
		await ExecuteAsync(
			client =>
			{
				EnsureCapabilitySupported(
					client.Capabilities.DisplayBoardFen,
					"engine FEN retrieval via the non-standard 'd' command"
				);
				return client.TryGetFenViaDisplayBoardAsync(ct);
			},
			ct
		).ConfigureAwait(false);

	public async Task<ImmutableArray<string>> GetLegalMovesAsync(CancellationToken ct = default) =>
		await ExecuteAsync(
			client =>
			{
				EnsureCapabilitySupported(
					client.Capabilities.PerftMoveListing,
					"legal-move enumeration via the non-standard 'go perft 1' command"
				);
				return client.GetLegalMovesViaPerftAsync(ct);
			},
			ct
		).ConfigureAwait(false);

	public async Task<SearchResult> SearchAsync(
		Fen                  fen,
		IEnumerable<string>? moves,
		SearchParameters     parameters,
		CancellationToken    ct = default) =>
		await ExecuteAsync(
			async client =>
			{
				await client.SetPositionAsync(fen, moves, ct).ConfigureAwait(false);
				return await client.GoAsync(parameters, ct).ConfigureAwait(false);
			},
			ct
		).ConfigureAwait(false);

	public async Task<SearchResult> SearchMoveAsync(
		Fen               fen,
		string            move,
		uint              depth,
		CancellationToken ct = default) =>
		await ExecuteAsync(
			async client =>
			{
				await client.SetPositionAsync(fen, null, ct).ConfigureAwait(false);
				return await client.GoAsync(
					new()
					{
						Depth = depth,
						SearchMoves = [move]
					},
					ct
				).ConfigureAwait(false);
			},
			ct
		).ConfigureAwait(false);

	public async ValueTask DisposeAsync()
	{
		await _client.DisposeAsync().ConfigureAwait(false);
		_gate.Dispose();
	}

	public void Dispose()
	{
		DisposeAsync().AsTask().GetAwaiter().GetResult();
	}

	private async Task ExecuteAsync(Func<UciEngineClient, Task> operation, CancellationToken ct)
	{
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			await operation(_client).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<T> ExecuteAsync<T>(Func<UciEngineClient, Task<T>> operation, CancellationToken ct)
	{
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			return await operation(_client).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private static void EnsureCapabilitySupported(UciCapabilityState capability, string capabilityName)
	{
		if (capability == UciCapabilityState.Supported)
			return;

		throw new NotSupportedException(
			$"The connected engine does not support {capabilityName}, which is required for this operation."
		);
	}
}
