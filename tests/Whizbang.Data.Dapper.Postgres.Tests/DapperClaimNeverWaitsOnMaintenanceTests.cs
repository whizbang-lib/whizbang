// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper coordinator keeps the two promises of #2611 as the EF Core one does: a claim whose statistics refresh cannot
/// analyze a stale queue table counts the skip by table and reason and warns once per back-off, and a queue-index rebuild
/// or a leftover's drop gives up within its lock timeout instead of waiting on an old transaction for hours, naming the
/// session it waited on. Twins on the EF Core driver: <c>ClaimWorkStaleStatisticsTests</c> and
/// <c>QueueIndexReindexSqlTests</c>.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.TableMaintenance.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/QueueStatisticsRefreshNotices.cs</code-under-test>
[Category("Integration")]
public class DapperClaimNeverWaitsOnMaintenanceTests : PostgresTestBase {
  private const string PKEY = "wh_active_streams_pkey";
  private static readonly TimeSpan _bound = TimeSpan.FromMilliseconds(500);

  [Test]
  [Timeout(300000)]
  public async Task AClaimWhoseRefreshCannotAnalyze_CountsTheSkip_AndWarnsOncePerBackoffAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await conn.ExecuteAsync("""
      ALTER TABLE wh_perspective_events SET (autovacuum_enabled = false);
      INSERT INTO wh_perspective_events (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number)
      VALUES (gen_random_uuid(), 'Stale.Perspective', gen_random_uuid(), 0, 0, NOW(), 0);
      DELETE FROM wh_perspective_events;
      ANALYZE wh_perspective_events;
      INSERT INTO wh_perspective_events (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number)
      SELECT s, 'Stale.Perspective', gen_random_uuid(), 0, 0, NOW(), compute_partition(s)
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, 5000)) g;
      SELECT pg_stat_force_next_flush();
      """, commandTimeout: 300);
    await conn.ExecuteAsync("SELECT 1");
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync(cancellationToken);
    await holder.ExecuteAsync("LOCK TABLE wh_perspective_events IN SHARE UPDATE EXCLUSIVE MODE", transaction: hold);
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var logger = new WarningLogger();
    var coordinator = new DapperWorkCoordinator(
      ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default }, logger, metrics: metrics);
    var poller = (Guid)TrackedGuid.New();
    await conn.ExecuteAsync("""
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@poller, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """, new { poller });
    var request = new ClaimWorkRequest(poller, "test", "test-host", 1, MaxStreams: 10);

    var batch = await coordinator.ClaimWorkAsync(request, cancellationToken);
    _ = await coordinator.ClaimWorkAsync(request, cancellationToken);
    await hold.RollbackAsync(cancellationToken);

    await Assert.That(batch.PerspectiveStreamIds).IsNotEmpty().Because("the claim goes on with the statistics it has");
    await Assert.That(_skipCounts(metrics)).IsEquivalentTo([(1L, (string?)"wh_perspective_events", (string?)"lock_not_available")]);
    await Assert.That(logger.Warnings.Count(w => w.Contains("wh_perspective_events", StringComparison.Ordinal)
        && w.Contains("could not be refreshed", StringComparison.Ordinal))).IsEqualTo(1)
      .Because($"the skip is warned once per back-off (warnings: {string.Join(" | ", logger.Warnings)})");
  }

  [Test]
  [Timeout(90000)]
  public async Task ARebuildBehindAnIdleTransaction_GivesUpWithinTheLockTimeout_AndNamesTheBlockerAsync(
      CancellationToken cancellationToken) {
    await using var holder = await _openAsync();
    var holderPid = await holder.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
    await using var idle = await holder.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
    _ = await holder.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_settings", transaction: idle);
    IWorkCoordinator coordinator = new DapperWorkCoordinator(
      ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default });

    var outcome = await coordinator.RebuildQueueIndexAsync(PKEY, _bound, cancellationToken);
    await idle.RollbackAsync(cancellationToken);
    var rebuilt = await coordinator.RebuildQueueIndexAsync(PKEY, _bound, cancellationToken);

    await Assert.That(outcome.GaveUp).IsTrue();
    await Assert.That(outcome.Blockers.Select(b => b.Pid)).Contains(holderPid);
    await Assert.That(rebuilt.GaveUp).IsFalse().Because("with the transaction gone the rebuild runs");
    await Assert.That(rebuilt.SizeAfterBytes.HasValue).IsTrue();
  }

  /// <summary>
  /// Under a role that owns the table but may not read other roles' activity: the session holding the table is still
  /// named (pg_locks is readable to every role), its transaction's age is reported as not visible, and the sessions the
  /// role cannot read at all are counted.
  /// </summary>
  [Test]
  [Timeout(90000)]
  public async Task ARebuildThatGaveUp_UnderARoleThatCannotReadOtherSessions_NamesTheLockHolderWithoutItsAgeAsync(
      CancellationToken cancellationToken) {
    var role = $"wh_owner_{Guid.NewGuid():N}"[..30];
    await using var admin = await _openAsync();
    await admin.ExecuteAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'owner';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT ALL ON ALL TABLES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      ALTER TABLE wh_active_streams OWNER TO {role};
      """);
    try {
      await using var holder = await _openAsync();
      var holderPid = await holder.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
      await using var hold = await holder.BeginTransactionAsync(cancellationToken);
      await holder.ExecuteAsync("LOCK TABLE wh_active_streams IN ROW EXCLUSIVE MODE", transaction: hold);
      IWorkCoordinator coordinator = new DapperWorkCoordinator(
        new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = "owner" }.ConnectionString,
        new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default });

      var outcome = await coordinator.RebuildQueueIndexAsync(PKEY, _bound, cancellationToken);
      await hold.RollbackAsync(cancellationToken);

      await Assert.That(outcome.GaveUp).IsTrue();
      await Assert.That(outcome.Blockers).Contains(new QueueIndexBlocker(holderPid, null))
        .Because("the lock holder is named from pg_locks, and its age is hidden from this role");
      await Assert.That(outcome.HiddenSessions).IsGreaterThan(0);
    } finally {
      await admin.ExecuteAsync($"ALTER TABLE wh_active_streams OWNER TO CURRENT_USER; DROP OWNED BY {role}; DROP ROLE {role};");
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task ALeftoversDropBehindATransactionHoldingTheTable_GivesUpWithinTheLockTimeoutAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await conn.ExecuteAsync("INSERT INTO wh_active_streams (stream_id, partition_number) VALUES (gen_random_uuid(), 7), (gen_random_uuid(), 7)");
    await Assert.That(async () => await conn.ExecuteAsync($"CREATE UNIQUE INDEX CONCURRENTLY {PKEY}_ccnew ON wh_active_streams (partition_number)"))
      .Throws<PostgresException>();
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync(cancellationToken);
    _ = await holder.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_active_streams", transaction: hold);
    IWorkCoordinator coordinator = new DapperWorkCoordinator(
      ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default });

    var thrown = await Assert.That(async () => await coordinator.DropInvalidQueueIndexAsync(PKEY + "_ccnew", _bound, cancellationToken))
      .Throws<PostgresException>();
    await hold.RollbackAsync(cancellationToken);
    var dropped = await coordinator.DropInvalidQueueIndexAsync(PKEY + "_ccnew", _bound, cancellationToken);

    await Assert.That(thrown!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable);
    await Assert.That(dropped).IsTrue().Because("with the transaction gone the drop runs");
  }

  private sealed class WarningLogger : ILogger<DapperWorkCoordinator> {
    private readonly List<string> _warnings = [];
    public List<string> Warnings { get { lock (_warnings) { return [.. _warnings]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (_warnings) { _warnings.Add(formatter(state, exception)); }
      }
    }
  }

  private static List<(long, string?, string?)> _skipCounts(WorkCoordinatorMetrics metrics) {
    var cells = new List<(long, string?, string?)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.QueueStatisticsRefreshSkips.Meter)
          && instrument.Name == "whizbang.work_coordinator.queue_statistics.refresh_skipped") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? table = null;
      string? reason = null;
      foreach (var tag in tags) {
        if (tag.Key == WorkCoordinatorMetrics.TABLE_TAG) {
          table = tag.Value as string;
        } else if (tag.Key == WorkCoordinatorMetrics.REASON_TAG) {
          reason = tag.Value as string;
        }
      }
      if (value > 0) {
        cells.Add((value, table, reason));
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return cells;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }
}
