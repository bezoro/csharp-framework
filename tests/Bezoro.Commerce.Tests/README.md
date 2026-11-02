# Commerce tests

`dotnet test tests/Bezoro.Commerce.Tests -c Release`

Covers catalog copying and validation, 2D/3D asset references, atomic purchase failure, idempotent purchase and grant retries, conflicting transaction keys, offer-time boundaries, ownership protection, free products, capacity exhaustion, integer overflow, complete snapshot restoration and an allocation check for warmed reads/equipment/retries. No payment provider or hosted database is required or simulated.
