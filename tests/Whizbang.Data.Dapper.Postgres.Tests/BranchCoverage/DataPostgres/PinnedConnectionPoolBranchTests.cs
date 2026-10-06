// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Diagnostics.Metrics;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The pinned pool's remaining decisions against a real server: a non-positive size, and the two
/// metrics it records when metrics are registered (a borrow's duration, and a borrow that timed out
/// waiting for a connection).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PinnedConnectionPool.cs</code-under-test>
public class PinnedConnectionPoolBranchTests : PostgresTestBase {

  // A misconfigured size of zero (or less) must give a working single-connection pool rather than a
  // pool that can never hand out a connection, and it must still be a pool of exactly one: a second
  // borrow while the first is held waits and then times out.
  [Test]
  [Timeout(30_000)]
  public async Task NonPositiveSize_BehavesAsASingleConnectionPoolAsync(CancellationToken cancellationToken) {
    var options = _options(size: 0);
    options.BorrowTimeoutMilliseconds = 500;
    await using var pool = new PinnedConnectionPool(options, _registry());

    var held = await pool.TryPinForAsync(typeof(PinnedWorker), cancellationToken);
    try {
      await Assert.That(held.Connection).IsNotNull();
      await Assert.That(held.Connection!.State).IsEqualTo(ConnectionState.Open);

      await Assert.That(async () => await pool.TryPinForAsync(typeof(PinnedWorker), cancellationToken))
        .Throws<OperationCanceledException>()
        .Because("the size is clamped to one, so the one connection is already held");
    } finally {
      await held.DisposeAsync();
    }
  }

  [Test]
  [Timeout(30_000)]
  public async Task Borrow_WithMetrics_RecordsTheBorrowDurationAsync(CancellationToken cancellationToken) {
    var metrics = new PinnedPoolMetrics();
    var recorded = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.BorrowDuration)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref recorded));
    listener.Start();
    await using var pool = new PinnedConnectionPool(_options(size: 1), _registry(), metrics);

    await using (var borrow = await pool.TryPinForAsync(typeof(PinnedWorker), cancellationToken)) {
      await Assert.That(borrow.Connection).IsNotNull();
    }

    await Assert.That(Volatile.Read(ref recorded)).IsEqualTo(1)
      .Because("each successful borrow records how long the caller waited for its connection");
  }

  [Test]
  [Timeout(30_000)]
  public async Task Borrow_TimingOutWithMetrics_CountsTheTimeoutAsync(CancellationToken cancellationToken) {
    var metrics = new PinnedPoolMetrics();
    var options = _options(size: 1);
    options.BorrowTimeoutMilliseconds = 500;
    await using var pool = new PinnedConnectionPool(options, _registry(), metrics);

    var held = await pool.TryPinForAsync(typeof(PinnedWorker), cancellationToken);
    try {
      await Assert.That(async () => await pool.TryPinForAsync(typeof(PinnedWorker), cancellationToken))
        .Throws<OperationCanceledException>();
    } finally {
      await held.DisposeAsync();
    }

    long timeouts = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.BorrowTimeouts.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => timeouts += value);
    listener.Start();
    listener.RecordObservableInstruments();

    await Assert.That(timeouts).IsEqualTo(1L)
      .Because("pool starvation is what the timeout counter exists to make visible");
  }

  private WhizbangPinnedPoolOptions _options(int size) => new() {
    Enabled = true,
    ConnectionString = ConnectionString,
    Size = size,
    IncludeFlushWorkers = false,
    BorrowTimeoutMilliseconds = 5_000,
    ConnectionLifetimeSeconds = 1_800,
  };

  private static PinnedWorkerRegistry _registry() {
    var registry = new PinnedWorkerRegistry();
    registry.AddOptIn(typeof(PinnedWorker));
    return registry;
  }

  /// <summary>Stand-in worker type used as the eligibility key.</summary>
  private sealed class PinnedWorker;
}
