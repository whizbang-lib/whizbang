// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2256: the open repair backlog, read against the real ledger, and the buckets opened and closed, counted where the
/// ledger functions insert and delete its rows. The ledger deletes a row when its bucket heals, so it never holds the
/// starting total; these readings are the only way to see completion and an ETA without querying each database.
/// </summary>
/// <remarks>
/// The accounting these tests lock: a bucket is opened when its row is recorded, or when a bucket marked unhealable
/// becomes repairable again (reopened); it is closed once, when its row is removed or when it is marked unhealable, and
/// removing a row already marked unhealable does not close it a second time. So the repairable rows change by exactly
/// <c>opened - closed</c>.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/392_TheLedgerCountsWhatItOpensAndCloses.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityLedgerBacklogSqlTests : EFCoreTestBase {
  private const int MAX_ATTEMPTS = 8;
  private const string EVENT_TYPE = "Contracts.BacklogProbe, Contracts";
  private const string OPENED = "whizbang.stream_integrity.ledger_buckets_opened";
  private const string CLOSED = "whizbang.stream_integrity.ledger_buckets_closed";

  // Fixed instants, not the host clock: the report takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  // ── the backlog read ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Backlog_CountsEachOriginsBucketsOncePerState_NamesTheOrigin_AndReadsTheQueueDepthAsync() {
    var (coordinator, _) = _coordinator();
    var named = TrackedGuid.New().Value;
    var unnamed = TrackedGuid.New().Value;
    var quiet = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    await _knownOriginAsync(conn, named, "orders");
    await _knownOriginAsync(conn, quiet, "billing");
    var streams = Enumerable.Range(0, 7).Select(_ => TrackedGuid.New().Value).ToArray();
    foreach (var stream in streams) {
      await _reportAsync(coordinator, named, stream, local: 0);
    }
    await _reportAsync(coordinator, unnamed, TrackedGuid.New().Value, local: 0);
    // One bucket in each state; the precedence decides the bucket that carries two marks.
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET unhealable_at = NOW(), exhausted_at = NOW() WHERE stream_id = $1", streams[0]);
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET exhausted_at = NOW() WHERE stream_id = $1", streams[1]);
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET repair_attempts = $2 WHERE stream_id = $1", streams[2], MAX_ATTEMPTS);
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET answer_pending_until = NOW() + INTERVAL '1 hour' WHERE stream_id = $1", streams[3]);
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET answered_at = NOW() WHERE stream_id = $1", streams[4]);
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET answer_pending_until = NOW() - INTERVAL '1 hour' WHERE stream_id = $1", streams[5]);
    await _deliveryAsync(conn);
    await _deliveryAsync(conn);

    var backlog = await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS);

    await Assert.That(backlog.Reported).IsTrue();
    await Assert.That(_count(backlog, "orders", LedgerBacklogStates.UNHEALABLE)).IsEqualTo(1L)
      .Because("unhealable is decided first: a bucket repair cannot change is not waiting on its budget");
    await Assert.That(_count(backlog, "orders", LedgerBacklogStates.EXHAUSTED)).IsEqualTo(2L)
      .Because("a bucket marked exhausted and one whose attempts reached the budget (written before the mark existed) both stopped asking");
    await Assert.That(_count(backlog, "orders", LedgerBacklogStates.WAITING)).IsEqualTo(1L);
    await Assert.That(_count(backlog, "orders", LedgerBacklogStates.ANSWERED)).IsEqualTo(1L);
    await Assert.That(_count(backlog, "orders", LedgerBacklogStates.OPEN)).IsEqualTo(2L)
      .Because("an answer wait that has passed is no longer waiting");
    await Assert.That(_count(backlog, unnamed.ToString(), LedgerBacklogStates.OPEN)).IsEqualTo(1L)
      .Because("an origin without a recorded name is reported by its id, never dropped");
    foreach (var state in _states) {
      await Assert.That(_count(backlog, "billing", state)).IsEqualTo(0L)
        .Because("a known origin with nothing open reads zero in every state, so a healed origin falls to zero");
    }
    await Assert.That(backlog.Buckets.Count).IsEqualTo(_states.Length * 3)
      .Because("every origin carries every state, each once");
    await Assert.That(backlog.OutboundDeliveries).IsEqualTo(2L);
  }

  [Test]
  public async Task Backlog_OfAnEmptyLedger_IsReported_WithNoBucketsAndAnEmptyQueueAsync() {
    var (coordinator, _) = _coordinator();

    var backlog = await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS);

    await Assert.That(backlog.Reported).IsTrue().Because("an empty ledger is a real reading of zero, not a missing one");
    await Assert.That(backlog.Buckets).IsEmpty();
    await Assert.That(backlog.OutboundDeliveries).IsEqualTo(0L);
  }

  [Test]
  public async Task Backlog_ThatCannotBeRead_IsNotReportedAsync() {
    var (coordinator, _) = _coordinator();
    await using var conn = await _openAsync();
    await _execAsync(conn, "SELECT drop_all_overloads('wh_integrity_ledger_backlog')");

    var backlog = await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS);

    await Assert.That(backlog.Reported).IsFalse()
      .Because("a failed read reports nothing; a zero would read as a healed service");
  }

  // ── opened and closed ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task AHealedBucket_IsClosedOnce_AndLowersTheOpenGaugeAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;

    await _reportAsync(coordinator, origin, stream, local: 0);
    await _reportAsync(coordinator, origin, stream, local: 0);
    await Assert.That(_how(metrics, OPENED, "new")).IsEqualTo(1L)
      .Because("the bucket's row was recorded once; a second sighting updates it");
    await Assert.That(_count(await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS), origin.ToString(), LedgerBacklogStates.OPEN))
      .IsEqualTo(1L);

    var ages = await coordinator.IntegrityMarkHealedBatchWithAgesAsync(origin, [_key(origin, stream)]);

    await Assert.That(ages!.Count).IsEqualTo(1).Because("the heal still reports its age for the heal histogram");
    await Assert.That(_how(metrics, CLOSED, "healed")).IsEqualTo(1L);
    await Assert.That(_count(await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS), origin.ToString(), LedgerBacklogStates.OPEN))
      .IsEqualTo(0L).Because("the heal removed the row, so the open gauge falls by it");
    _ = await coordinator.IntegrityMarkHealedBatchWithAgesAsync(origin, [_key(origin, stream)]);
    await Assert.That(_how(metrics, CLOSED, "healed")).IsEqualTo(1L).Because("a heal that removes nothing closes nothing");
  }

  [Test]
  public async Task AnUnhealableBucket_MovesToStateUnhealable_AndIsNotClosedAgainWhenItsRowGoesAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var unhealable = TrackedGuid.New().Value;
    var repairable = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, unhealable, local: 0);
    await _reportAsync(coordinator, origin, repairable, local: 0);

    var marked = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [_key(origin, unhealable)], _t0);

    await Assert.That(marked!.Count).IsEqualTo(1);
    var backlog = await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS);
    await Assert.That(_count(backlog, origin.ToString(), LedgerBacklogStates.UNHEALABLE)).IsEqualTo(1L)
      .Because("the bucket stays on the ledger, in the unhealable state, not in the open one");
    await Assert.That(_count(backlog, origin.ToString(), LedgerBacklogStates.OPEN)).IsEqualTo(1L);

    var ages = await coordinator.IntegrityMarkTypesHealedWithAgesAsync(origin, [_key(origin, Guid.Empty)]);

    await Assert.That(ages!.Count).IsEqualTo(2).Because("both rows of the proven type are forgotten, each with its age");
    await Assert.That(_how(metrics, CLOSED, "healed")).IsEqualTo(1L)
      .Because("the unhealable bucket was closed when it was marked; only the repairable one closes now");
  }

  [Test]
  public async Task TheSingleKeyPaths_CountTheRowsTheyOpenAndCloseAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var reported = _key(origin, TrackedGuid.New().Value);
    var repaired = _key(origin, TrackedGuid.New().Value);

    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(reported, 11, 21, 0, 0, _t0, _cooldown)).IsTrue();
    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(reported, 11, 21, 0, 0, _t0, _cooldown)).IsFalse()
      .Because("inside the cooldown the second sighting is not reported, as before");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(repaired, _t0, TimeSpan.FromSeconds(30), MAX_ATTEMPTS)).IsTrue();
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(repaired, _t0, TimeSpan.FromSeconds(30), MAX_ATTEMPTS)).IsFalse()
      .Because("the backoff holds the second request, as before");
    await Assert.That(_how(metrics, OPENED, "new")).IsEqualTo(2L)
      .Because("a report and a repair each recorded one new row");

    await coordinator.IntegrityMarkHealedAsync(reported);
    await coordinator.IntegrityMarkHealedAsync(reported);

    await Assert.That(_how(metrics, CLOSED, "healed")).IsEqualTo(1L);
  }

  [Test]
  public async Task TheRepairBatch_CountsTheRowsItOpensAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var tracked = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, tracked, local: 0);
    var keys = new[] { _key(origin, tracked), _key(origin, TrackedGuid.New().Value), _key(origin, TrackedGuid.New().Value) };

    var grants = await coordinator.IntegrityTryBeginRepairBatchAsync(origin, keys, _t0, TimeSpan.FromSeconds(30), MAX_ATTEMPTS, maxGrants: 2);

    await Assert.That(grants!.ToArray()).IsEquivalentTo(new[] { true, true, false })
      .Because("the grant cap still stops the batch in order");
    await Assert.That(_how(metrics, OPENED, "new")).IsEqualTo(2L)
      .Because("the report recorded one row and the batch one more; the key past the cap was not consulted");
  }

  [Test]
  public async Task AnUnhealableBucketWhoseLocalSideChanges_IsReopenedAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var batched = TrackedGuid.New().Value;
    var single = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, batched, local: 0);
    await _reportAsync(coordinator, origin, single, local: 0);
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [_key(origin, batched), _key(origin, single)], _t0);

    await _reportAsync(coordinator, origin, batched, local: 5);
    _ = await coordinator.IntegrityTryBeginReportAsync(_key(origin, single), 11, 21, 7, 7, _t0, _cooldown);

    await Assert.That(_how(metrics, OPENED, "reopened")).IsEqualTo(2L)
      .Because("a changed local side lifts the unhealable mark, and repair can act on the bucket again");
    await Assert.That(_count(await coordinator.GetIntegrityLedgerBacklogAsync(MAX_ATTEMPTS), origin.ToString(), LedgerBacklogStates.OPEN))
      .IsEqualTo(2L);
  }

  [Test]
  public async Task AnUnhealableBucketWhoseDeficitGrows_IsReopenedAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, stream, local: 0, deficit: 3);
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [_key(origin, stream)], _t0);

    await _reportAsync(coordinator, origin, stream, local: 0, deficit: 3);
    await Assert.That(_how(metrics, OPENED, "reopened")).IsEqualTo(0L).Because("the same deficit changes nothing");
    await _reportAsync(coordinator, origin, stream, local: 0, deficit: 5);

    await Assert.That(_how(metrics, OPENED, "reopened")).IsEqualTo(1L)
      .Because("a grown deficit resets the ladder and lifts the unhealable mark (320)");
  }

  [Test]
  public async Task TheForgetPaths_CountRowsThatWereStillOpen_AsForgottenAsync() {
    var (coordinator, metrics) = _coordinator();
    var origin = TrackedGuid.New().Value;
    var unhealable = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, TrackedGuid.New().Value, local: 0);
    await _reportAsync(coordinator, origin, unhealable, local: 0);
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [_key(origin, unhealable)], _t0);

    var unconsumed = await coordinator.IntegrityForgetUnconsumedEventTypesAsync(["Contracts.Other, Contracts"], []);

    await Assert.That(unconsumed).IsEqualTo(2).Because("the prune still answers every row it removed");
    await Assert.That(_how(metrics, CLOSED, "forgotten")).IsEqualTo(1L)
      .Because("the unhealable row was closed when it was marked");

    var other = TrackedGuid.New().Value;
    await _reportAsync(coordinator, other, TrackedGuid.New().Value, local: 0);
    var nonOwner = await coordinator.IntegrityForgetNonOwnerLedgerRowsAsync((o, _, _) => o == other);

    await Assert.That(nonOwner).IsEqualTo(1);
    await Assert.That(_how(metrics, CLOSED, "forgotten")).IsEqualTo(2L);
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  private static readonly string[] _states = [
    LedgerBacklogStates.UNHEALABLE, LedgerBacklogStates.EXHAUSTED, LedgerBacklogStates.WAITING,
    LedgerBacklogStates.ANSWERED, LedgerBacklogStates.OPEN,
  ];

  private static long _count(LedgerBacklogSnapshot backlog, string origin, string state) =>
    backlog.Buckets.Where(b => b.Origin == origin && b.State == state).Sum(b => b.Buckets);

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream) =>
    new(origin, null, EVENT_TYPE, stream);

  private (EFCoreWorkCoordinator<WorkCoordinationDbContext> Coordinator, StreamIntegrityMetrics Metrics) _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    return (new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(),
      integrityMetrics: metrics), metrics);
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private static async Task _reportAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, Guid stream, long local, long deficit = 3) {
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(_key(origin, stream), 11, 21, local, local, IsDeficit: true, DeficitEvents: deficit)],
      _t0, _cooldown) ?? throw new InvalidOperationException("the report batch failed");
  }

  private static long _how(StreamIntegrityMetrics metrics, string instrument, string how) {
    long total = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => {
        if (i.Meter == metrics.LedgerBucketsOpened.Meter && i.Name == instrument) {
          l.EnableMeasurementEvents(i);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      foreach (var tag in tags) {
        if (tag.Key == "how" && Equals(tag.Value, how)) {
          total += value;
        }
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return total;
  }

  private static Task _knownOriginAsync(NpgsqlConnection conn, Guid origin, string name) =>
    _execAsync(conn,
      "INSERT INTO wh_integrity_known_origins (origin_service_id, origin_service_name, last_checkpoint_at) VALUES ($1, $2, NOW())",
      origin, name);

  private static Task _deliveryAsync(NpgsqlConnection conn) =>
    _execAsync(conn,
      "INSERT INTO wh_integrity_deliveries (delivery_id, delivery_type, payload) VALUES ($1, 'probe', '{}'::jsonb)",
      TrackedGuid.New().Value);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
