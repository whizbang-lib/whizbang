using System.Data;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Integration coverage for <see cref="PinnedConnectionPool"/> against a real
/// PostgreSQL container. Slice 6 of the pinned-pool PR.
/// </summary>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public class PinnedConnectionPoolIntegrationTests : PostgresTestBase {

  [Test]
  public async Task RealPool_BorrowAndDispose_RoundTripsConnectionAsync() {
    var opts = _options(size: 2);
    await using var pool = new PinnedConnectionPool(opts, _registry(typeof(_pinnedWorker)));

    await using var borrow = await pool.TryPinForAsync(typeof(_pinnedWorker), CancellationToken.None);

    await Assert.That(borrow.Connection).IsNotNull()
      .Because("Eligible worker against an enabled pool MUST receive an open conn.");
    await Assert.That(borrow.Connection!.State).IsEqualTo(ConnectionState.Open);

    // Round-trip a trivial query to prove the conn actually talks to PG.
    await using var cmd = borrow.Connection.CreateCommand();
    cmd.CommandText = "SELECT 1";
    var result = await cmd.ExecuteScalarAsync();
    await Assert.That(Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1);
  }

  [Test]
  public async Task RealPool_IneligibleWorker_ReturnsNoOpBorrowAsync() {
    var opts = _options(size: 1);
    opts.ExcludeWorkers.Add(nameof(_pinnedWorker));
    await using var pool = new PinnedConnectionPool(opts, _registry(typeof(_pinnedWorker)));

    await using var borrow = await pool.TryPinForAsync(typeof(_pinnedWorker), CancellationToken.None);

    await Assert.That(borrow.Connection).IsNull()
      .Because("ExcludeWorkers naming this worker MUST yield a no-op borrow with no actual pool conn acquired.");
  }

  [Test]
  [Timeout(15_000)]
  public async Task RealPool_Size1_SecondBorrowBlocksUntilFirstDisposesAsync(CancellationToken ct) {
    var opts = _options(size: 1);
    opts.BorrowTimeoutMilliseconds = 8_000;
    await using var pool = new PinnedConnectionPool(opts, _registry(typeof(_pinnedWorker)));

    var first = await pool.TryPinForAsync(typeof(_pinnedWorker), ct);

    var secondTask = pool.TryPinForAsync(typeof(_pinnedWorker), ct).AsTask();

    // Second borrow is in flight, waiting on the first to release.
    var raced = await Task.WhenAny(secondTask, Task.Delay(500, ct));
    await Assert.That(ReferenceEquals(raced, secondTask)).IsFalse()
      .Because("Size=1 means the second borrow MUST block while the first is held — proving serialization.");

    // Release the first borrow.
    await first.DisposeAsync();

    var second = await secondTask;
    try {
      await Assert.That(second.Connection).IsNotNull()
        .Because("After the first borrow releases, the second MUST acquire a healthy conn.");
    } finally {
      await second.DisposeAsync();
    }
  }

  [Test]
  [Timeout(15_000)]
  public async Task RealPool_Size1_BorrowTimeout_ThrowsOperationCanceledAsync(CancellationToken ct) {
    var opts = _options(size: 1);
    opts.BorrowTimeoutMilliseconds = 500;
    await using var pool = new PinnedConnectionPool(opts, _registry(typeof(_pinnedWorker)));

    var held = await pool.TryPinForAsync(typeof(_pinnedWorker), ct);
    try {
      await Assert.That(async () => await pool.TryPinForAsync(typeof(_pinnedWorker), ct))
        .Throws<OperationCanceledException>()
        .Because("With Size=1 already held and BorrowTimeoutMilliseconds=500, the second borrow MUST time out — confirming the timeout knob is wired.");
    } finally {
      await held.DisposeAsync();
    }
  }

  [Test]
  public async Task RealPool_DisposeReusesConnection_NoDiscardAllOnSuccessivePinAsync() {
    // Two borrows on Size=1 against the same pool. With Npgsql's MinPool=MaxPool=Size, the
    // SAME physical conn is handed out each time. A pg_stat-derived assertion against
    // DISCARD ALL count would need superuser; instead we assert the Npgsql ProcessID
    // is identical, which is only possible when the underlying conn is the same.
    var opts = _options(size: 1);
    await using var pool = new PinnedConnectionPool(opts, _registry(typeof(_pinnedWorker)));

    int? firstPid = null;
    int? secondPid = null;

    await using (var first = await pool.TryPinForAsync(typeof(_pinnedWorker), CancellationToken.None)) {
      firstPid = (first.Connection as NpgsqlConnection)?.ProcessID;
    }
    await using (var second = await pool.TryPinForAsync(typeof(_pinnedWorker), CancellationToken.None)) {
      secondPid = (second.Connection as NpgsqlConnection)?.ProcessID;
    }

    await Assert.That(firstPid).IsNotNull();
    await Assert.That(firstPid).IsEqualTo(secondPid)
      .Because("MinPoolSize=MaxPoolSize=1 means the Npgsql pool holds exactly one physical conn. Successive borrow + dispose MUST hand out the same conn — that's the DISCARD-ALL-skip guarantee at the SDK level.");
  }

  [Test]
  public async Task RealPool_PinnedConnFlowsThroughCoordinatorScopeAsync() {
    // Verifies the end-to-end wiring: pin via pool → push to PinnedConnectionContext →
    // CoordinatorConnectionScope.AcquireAsync sees it → returns the pinned (not a fresh) conn.
    var opts = _options(size: 1);
    await using var pool = new PinnedConnectionPool(opts, _registry(typeof(_pinnedWorker)));

    await using var borrow = await pool.TryPinForAsync(typeof(_pinnedWorker), CancellationToken.None);
    using var ctx = PinnedConnectionContext.Push(borrow.Connection);

    await using var scope = await CoordinatorConnectionScope.AcquireAsync(
      "Host=ignored;Database=ignored;Username=x;Password=x;Timeout=1",
      CancellationToken.None);

    await Assert.That(scope.Connection).IsSameReferenceAs(borrow.Connection!)
      .Because("Pinned conn in context MUST be returned by AcquireAsync without touching the fresh-conn-string fallback.");

    // Dispose the SCOPE — the conn MUST stay open (pool owns it).
    await scope.DisposeAsync();
    await Assert.That(borrow.Connection!.State).IsEqualTo(ConnectionState.Open)
      .Because("Coordinator scope MUST NOT close a pinned conn it didn't open — the pool owns lifetime.");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private WhizbangPinnedPoolOptions _options(int size) => new() {
    Enabled = true,
    ConnectionString = ConnectionString,
    Size = size,
    IncludeFlushWorkers = false,
    BorrowTimeoutMilliseconds = 5_000,
    ConnectionLifetimeSeconds = 1_800,
  };

  private static PinnedWorkerRegistry _registry(Type optInWorker) {
    var registry = new PinnedWorkerRegistry();
    registry.AddOptIn(optInWorker);
    return registry;
  }

  /// <summary>Stand-in worker type used as the eligibility key; not a real BackgroundService.</summary>
  private sealed class _pinnedWorker { }
}
