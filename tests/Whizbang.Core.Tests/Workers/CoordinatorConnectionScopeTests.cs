#pragma warning disable CA1707

using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Path-discrimination tests for <see cref="CoordinatorConnectionScope"/>.
/// The lifecycle and end-to-end correctness assertions (pinned conn not
/// disposed, DbContext-owned conn left intact) need a real PostgreSQL
/// server and are covered by the integration tests in Slice 6.
/// </summary>
/// <remarks>
/// <para>
/// <c>NpgsqlConnection</c> is sealed and a real network round-trip is
/// required to take the State = Open path. These unit tests therefore
/// verify the BRANCH taken by the helper, by deliberately pointing
/// the pinned conn and the fresh-conn-string at distinct unreachable
/// hosts. The exception that surfaces names the host that was attempted
/// — that uniquely identifies which branch ran.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public class CoordinatorConnectionScopeTests {

  [Test]
  public async Task AcquireAsync_NoPinnedContext_AttemptsToOpenFreshConnAsync() {
    // No pin in context. The Dapper path should construct a fresh conn from
    // the fresh-string and try to open it — both succeeding (which we can't
    // verify in unit) or failing against the unreachable host (which we CAN).
    var freshUnreachable = $"Host=127.0.0.99;Port=1;Database=fresh-sentinel;Username=x;Password=x;Timeout=1;ApplicationName=FRESH_PATH_SENTINEL";

    NpgsqlException? thrown = null;
    try {
      await using var scope = await CoordinatorConnectionScope.AcquireAsync(freshUnreachable, CancellationToken.None);
    } catch (NpgsqlException ex) {
      thrown = ex;
    }

    await Assert.That(thrown).IsNotNull()
      .Because("With no pinned context AND an unreachable connection string, AcquireAsync MUST attempt to open and surface the Npgsql failure — confirming it took the fresh-conn path, not the no-op path.");
  }

  [Test]
  public async Task AcquireAsync_PinnedContextSet_PrefersPinnedOverFreshStringAsync() {
    // Pinned conn points at one unreachable host; the fresh-conn string points at a different
    // unreachable host. Whichever host appears in the resulting exception identifies the
    // branch the helper took. We assert the pinned host won.
    var pinnedString = "Host=192.0.2.10;Port=1;Database=pinned-sentinel;Username=x;Password=x;Timeout=1;ApplicationName=PINNED_SENTINEL";
    var freshString = "Host=192.0.2.20;Port=2;Database=fresh-sentinel;Username=x;Password=x;Timeout=1;ApplicationName=FRESH_SENTINEL";
    var pinned = new NpgsqlConnection(pinnedString);
    using var _ = PinnedConnectionContext.Push(pinned);

    NpgsqlException? thrown = null;
    try {
      await using var scope = await CoordinatorConnectionScope.AcquireAsync(freshString, CancellationToken.None);
    } catch (NpgsqlException ex) {
      thrown = ex;
    }

    await Assert.That(thrown).IsNotNull()
      .Because("The pinned conn (also unreachable) was selected for opening; the open attempt MUST fail.");
    // Discriminate by which host name shows up in the failure. Npgsql exception messages
    // include the host being connected to.
    await Assert.That(thrown!.ToString()).Contains("192.0.2.10")
      .Because("Exception MUST mention the PINNED host (192.0.2.10), proving the helper took the pinned branch — not the 192.0.2.20 fresh-string branch.");
  }

  [Test]
  public async Task AcquireForEfCoreAsync_NoPinnedContext_TriesToOpenSuppliedDbContextConnAsync() {
    // EF Core path with no pin → use the supplied DbContext conn directly. Use an unreachable
    // conn so the open attempt surfaces as an exception (path identified).
    var dbContextConn = new NpgsqlConnection("Host=192.0.2.30;Port=1;Database=dbctx-sentinel;Username=x;Password=x;Timeout=1;ApplicationName=DBCTX_SENTINEL");

    NpgsqlException? thrown = null;
    try {
      await using var scope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(dbContextConn, CancellationToken.None);
    } catch (NpgsqlException ex) {
      thrown = ex;
    }

    await Assert.That(thrown).IsNotNull();
    await Assert.That(thrown!.ToString()).Contains("192.0.2.30")
      .Because("With no pin, AcquireForEfCoreAsync MUST attempt to open the supplied DbContext conn — confirmed by the dbctx host appearing in the exception.");
  }

  [Test]
  public async Task AcquireForEfCoreAsync_PinnedContextSet_PrefersPinnedOverDbContextConnAsync() {
    var pinned = new NpgsqlConnection("Host=192.0.2.40;Port=1;Database=pinned-sentinel;Username=x;Password=x;Timeout=1;ApplicationName=PINNED_SENTINEL");
    var dbContextConn = new NpgsqlConnection("Host=192.0.2.50;Port=2;Database=dbctx-sentinel;Username=x;Password=x;Timeout=1;ApplicationName=DBCTX_SENTINEL");
    using var _ = PinnedConnectionContext.Push(pinned);

    NpgsqlException? thrown = null;
    try {
      await using var scope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(dbContextConn, CancellationToken.None);
    } catch (NpgsqlException ex) {
      thrown = ex;
    }

    await Assert.That(thrown).IsNotNull();
    await Assert.That(thrown!.ToString()).Contains("192.0.2.40")
      .Because("Pinned conn (192.0.2.40) MUST win over the supplied DbContext conn (192.0.2.50) on the EF Core path — otherwise the pin is lost.");
  }

  [Test]
  public async Task AcquireAsync_NullConnectionString_ThrowsAsync() {
    await Assert.That(async () =>
        await CoordinatorConnectionScope.AcquireAsync(connectionString: null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AcquireForEfCoreAsync_NullDbContextConn_ThrowsAsync() {
    await Assert.That(async () =>
        await CoordinatorConnectionScope.AcquireForEfCoreAsync(dbContextConnection: null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AcquireAsync_CancelledToken_PropagatesOperationCanceledAsync() {
    var connString = "Host=192.0.2.60;Port=1;Database=x;Username=x;Password=x;Timeout=1";
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () =>
        await CoordinatorConnectionScope.AcquireAsync(connString, cts.Token))
      .Throws<OperationCanceledException>()
      .Because("Already-cancelled CT MUST short-circuit before any network round-trip — workers rely on CT for graceful shutdown.");
  }
}
