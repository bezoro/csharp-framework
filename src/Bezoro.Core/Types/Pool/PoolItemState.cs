namespace Bezoro.Core.Types.Pool;

internal enum PoolItemState
{
	Available,
	Renting,
	Rented,
	Returning,
	Discarding
}
