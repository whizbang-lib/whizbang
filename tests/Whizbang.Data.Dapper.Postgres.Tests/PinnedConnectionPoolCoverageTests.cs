using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="PinnedConnectionPool"/> paths that
/// <see cref="PinnedConnectionPoolIntegrationTests"/> doesn't reach: the constructor's guard
/// against a missing connection string, and idempotent disposal of the pool itself. Neither needs
/// a live Postgres — <c>NpgsqlDataSource.Create</c> only parses/builds a pool descriptor, it does
/// not connect until something actually borrows from it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PinnedConnectionPool.cs</code-under-test>
public class PinnedConnectionPoolCoverageTests {

  // Silently limping along with no connection string would leave every borrow attempt failing
  // later with a confusing Npgsql error instead of a clear registration-time one. Failing fast
  // here is also what tells an operator to register NoOpPinnedConnectionPool instead when the
  // feature is meant to stay off.
  [Test]
  public async Task Constructor_WithEmptyConnectionString_ThrowsInvalidOperationExceptionAsync() {
    var options = new WhizbangPinnedPoolOptions {
      Enabled = true,
      ConnectionString = "   ",
      Size = 1,
    };
    var registry = new PinnedWorkerRegistry();

    await Assert.That(() => new PinnedConnectionPool(options, registry))
      .Throws<InvalidOperationException>()
      .Because("a pool with no usable connection string must fail at construction, not on the first borrow");
  }

  // A pool is typically released via `await using`; a caller path that also disposes explicitly,
  // or a shutdown sequence that disposes it more than once, must not attempt a second disposal of
  // the underlying NpgsqlDataSource.
  [Test]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync() {
    var options = new WhizbangPinnedPoolOptions {
      Enabled = true,
      ConnectionString = "Host=localhost;Database=coverage;Username=coverage;Password=coverage",
      Size = 1,
    };
    var registry = new PinnedWorkerRegistry();
    var pool = new PinnedConnectionPool(options, registry);

    await pool.DisposeAsync();

    await Assert.That(async () => await pool.DisposeAsync()).ThrowsNothing()
      .Because("a second Dispose on the same pool must be a no-op, not a second attempt to dispose the underlying data source");
  }
}

/// <summary>
/// Coverage for the one <see cref="PinnedConnectionPool"/> disposal path that DOES need a real
/// borrowed connection: an active (not no-op) borrow handle disposed twice.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PinnedConnectionPool.cs</code-under-test>
public class PinnedConnectionPoolActiveBorrowCoverageTests : PostgresTestBase {

  /// <summary>Stand-in worker type used as the eligibility key; not a real BackgroundService.</summary>
  private sealed class _pinnedWorker { }

  // A borrowed connection is normally released via `await using`; a caller path that also
  // disposes explicitly must not attempt a second return-to-pool of a connection that's already
  // back in the pool -- doing so risks handing the same physical connection out twice.
  [Test]
  public async Task ActiveBorrow_DisposedTwice_IsIdempotentAsync() {
    var options = new WhizbangPinnedPoolOptions {
      Enabled = true,
      ConnectionString = ConnectionString,
      Size = 1,
    };
    var registry = new PinnedWorkerRegistry();
    registry.AddOptIn(typeof(_pinnedWorker));
    await using var pool = new PinnedConnectionPool(options, registry);

    var borrow = await pool.TryPinForAsync(typeof(_pinnedWorker), CancellationToken.None);
    await Assert.That(borrow.Connection).IsNotNull()
      .Because("an eligible worker against an enabled pool must receive an open connection to actually exercise the active borrow path");

    await borrow.DisposeAsync();

    await Assert.That(async () => await borrow.DisposeAsync()).ThrowsNothing()
      .Because("a second Dispose on an active borrow must be a no-op, not a second attempt to dispose the connection it already returned to the pool");
  }
}
