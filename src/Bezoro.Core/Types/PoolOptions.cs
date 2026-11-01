namespace Bezoro.Core.Types;

/// <summary>
///     Configuration options for object pool behavior.
/// </summary>
/// <param name="InitialCapacity">Initial number of pre-created objects. Default is 0.</param>
/// <param name="MaxCapacity">Maximum pool capacity. Use -1 for unbounded or zero to accept no objects. Default is -1.</param>
/// <param name="EnableAsyncWait">Whether to allow async waiting when pool is exhausted. Default is <c>true</c>.</param>
/// <param name="ValidateOnRent">Whether to call Validate before returning rented objects. Default is <c>true</c>.</param>
/// <param name="TrackStatistics">Whether to enable detailed statistics collection. Default is <c>false</c>.</param>
public readonly record struct PoolOptions(
	int      InitialCapacity  = 0,
	int      MaxCapacity      = -1,
	bool     EnableAsyncWait  = true,
	bool     ValidateOnRent   = true,
	bool     TrackStatistics  = false
)
{
	private readonly bool _configured = true;
	internal bool IsConfigured => _configured;

	/// <summary>Creates the same configuration as <see cref="Default" />.</summary>
	public PoolOptions() : this(0, -1, true, true, false) { }

	/// <summary>
	///     Default options with sensible production defaults.
	/// </summary>
	public static PoolOptions Default => new(
		MaxCapacity: -1,
		EnableAsyncWait: true,
		ValidateOnRent: true
	);

	/// <summary>
	///     Options optimized for high-throughput scenarios.
	/// </summary>
	public static PoolOptions HighThroughput => new(
		Environment.ProcessorCount * 2,
		Environment.ProcessorCount * 8,
		ValidateOnRent: false,
		TrackStatistics: false
	);
}
