# Player profile tests

`dotnet test tests/Bezoro.PlayerProfiles.Tests -c Release`

Covers profile identity validation, match-history duplicate and conflict behavior, full-page retry protection, owner validation, copied history ordering, published mathematical Elo examples, unequal K factors, extreme finite inputs and counter/overflow failures. Concurrent append/retry/lookup and coherent snapshot tests verify one record per identity and no lost records. The module has no service integration tests because authentication, persistent history and global rankings belong to the host.
