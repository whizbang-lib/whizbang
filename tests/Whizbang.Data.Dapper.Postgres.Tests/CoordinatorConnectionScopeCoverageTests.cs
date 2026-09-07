using System.Data;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for the "pinned connection exists but is not yet open" fall-through in
/// <see cref="CoordinatorConnectionScope"/>. <see cref="CoordinatorConnectionScopeTests"/> (in
/// Whizbang.Core.Tests) already pins the BRANCH the guard takes by pointing an unreachable host
/// at it, but every one of those attempts fails to open — the open call itself is covered, but
/// falling through the guard's closing brace to actually hand back an OPEN pinned connection
/// never happens there. <see cref="PinnedConnectionPoolIntegrationTests"/> covers the opposite
/// gap: its pool-borrowed connection is already open by the time it reaches the scope, so the
/// guard's body never runs at all. Neither existing suite drives "pinned, not yet open, and the
/// open actually succeeds" — which needs a real reachable Postgres, hence this file.
/// <para>
/// A caller acquiring a "connection" through this scope trusts it back as usable immediately.
/// If a not-yet-open pinned connection were ever handed back unopened, the very next query
/// issued against it would fail with an opaque "connection is closed" error far from the real
/// cause.
/// </para>
/// </summary>
public class CoordinatorConnectionScopeCoverageTests : PostgresTestBase {

  [Test]
  public async Task AcquireAsync_PinnedConnectionNotYetOpen_OpensItAndReturnsTheSameReferenceAsync() {
    // Deliberately NOT opened before pushing — starts ConnectionState.Closed, so AcquireAsync
    // must be the one to open it.
    var pinned = new NpgsqlConnection(ConnectionString);
    using var pinnedScope = PinnedConnectionContext.Push(pinned);

    try {
      await using var scope = await CoordinatorConnectionScope.AcquireAsync(
        "Host=ignored;Database=ignored;Username=ignored;Password=ignored;Timeout=1",
        CancellationToken.None);

      await Assert.That(scope.Connection).IsSameReferenceAs(pinned)
        .Because("a pinned connection in context must win over the fresh-connection-string fallback");
      await Assert.That(scope.Connection.State).IsEqualTo(ConnectionState.Open)
        .Because("a caller that gets the scope back must be able to query immediately — a closed "
               + "connection handed back as \"acquired\" would fail on the caller's very next statement");
    } finally {
      await pinned.CloseAsync();
    }
  }

  [Test]
  public async Task AcquireForEfCoreAsync_PinnedConnectionNotYetOpen_OpensItAndReturnsTheSameReferenceAsync() {
    var pinned = new NpgsqlConnection(ConnectionString);
    using var pinnedScope = PinnedConnectionContext.Push(pinned);
    // A distinct, never-opened DbContext connection the pinned path must NOT touch.
    await using var dbContextConnection = new NpgsqlConnection(ConnectionString);

    try {
      await using var scope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(
        dbContextConnection, CancellationToken.None);

      await Assert.That(scope.Connection).IsSameReferenceAs(pinned)
        .Because("the EF Core path must also prefer the pinned connection over the supplied DbContext one");
      await Assert.That(scope.Connection.State).IsEqualTo(ConnectionState.Open)
        .Because("same contract as the Dapper path — a not-yet-open pinned connection must be opened "
               + "before being handed back, not returned closed");
      await Assert.That(dbContextConnection.State).IsEqualTo(ConnectionState.Closed)
        .Because("the DbContext connection must be left untouched when the pinned connection wins");
    } finally {
      await pinned.CloseAsync();
    }
  }
}
