// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2544: the database side of filling the origin positions of received events stored without one (migration 561): the
/// receiver's bounded walk over its held rows, the origin's lookup, the fill (NULL positions only, sealed epochs refolded,
/// unknown answers recorded, reordered streams rewound), the unfilled count, and the maintenance step that asks.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
[Category("Integration")]
[Category("Shard4")]
public class OriginPositionFillSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.PositionFillEvent, Whizbang.Tests";
  private const int REWIND_REQUIRED = 32;
  private static readonly DateTimeOffset _t0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

  // ── the walk ─────────────────────────────────────────────────────────

  [Test]
  public async Task Ask_WalksOneOriginInBoundedBatches_AndRestartsTheLapOnlyAfterThePauseAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var held = new List<Guid>();
    for (var i = 0; i < 5; i++) {
      held.Add(await _seedAsync(conn, origin, null, minutesAgo: 60 - i));
    }
    await _seedAsync(conn, origin, 7, minutesAgo: 30);
    var store = _store();

    var first = await store.AskAsync(2, TimeSpan.FromMinutes(30), _t0, CancellationToken.None);
    var second = await store.AskAsync(2, TimeSpan.FromMinutes(30), _t0, CancellationToken.None);
    var third = await store.AskAsync(2, TimeSpan.FromMinutes(30), _t0, CancellationToken.None);

    await Assert.That(first!.EventIds).IsEquivalentTo(held.Take(2).ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(first.LapEnded).IsFalse();
    await Assert.That(second!.EventIds).IsEquivalentTo(held.Skip(2).Take(2).ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the cursor is durable: the next batch starts after the last row the previous one read");
    await Assert.That(third!.EventIds).IsEquivalentTo([held[4]]).Because("the positioned event is never asked for");
    await Assert.That(third.LapEnded).IsTrue();
    await Assert.That(await store.AskAsync(2, TimeSpan.FromMinutes(30), _t0.AddMinutes(29), CancellationToken.None)).IsNull()
      .Because("a lap that reached the end waits out its pause, so an unstamped event is not asked in a tight loop");
    var nextLap = await store.AskAsync(2, TimeSpan.FromMinutes(30), _t0.AddMinutes(30), CancellationToken.None);
    await Assert.That(nextLap!.EventIds).IsEquivalentTo(held.Take(2).ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("after the pause the walk starts over from the oldest row still held");
  }

  [Test]
  public async Task Ask_ServesTheOriginServedLeastRecently_AndLeavesOutUnknownAnswersAsync() {
    await using var conn = await _openAsync();
    var a = TrackedGuid.New().Value;
    var b = TrackedGuid.New().Value;
    var a1 = await _seedAsync(conn, a, null, minutesAgo: 50);
    var a2 = await _seedAsync(conn, a, null, minutesAgo: 49);
    var b1 = await _seedAsync(conn, b, null, minutesAgo: 48);
    await _fillAsync(conn, a, [], [], unknown: [a1]);
    var store = _store();

    var asks = new List<OriginPositionAsk>();
    for (var i = 0; i < 2; i++) {
      asks.Add((await store.AskAsync(10, TimeSpan.FromMinutes(30), _t0.AddSeconds(i), CancellationToken.None))!);
    }

    await Assert.That(asks.ConvertAll(x => x.OriginServiceId)).IsEquivalentTo(new[] { a, b }.ToList())
      .Because("each origin is served in turn, so one origin's long history never starves another");
    await Assert.That(asks.Single(x => x.OriginServiceId == a).EventIds).IsEquivalentTo([a2])
      .Because("an event the origin answered unknown is never asked again");
    await Assert.That(asks.Single(x => x.OriginServiceId == b).EventIds).IsEquivalentTo([b1]);
    await Assert.That(await store.AskAsync(10, TimeSpan.FromMinutes(30), _t0.AddSeconds(5), CancellationToken.None)).IsNull();
  }

  [Test]
  public async Task Ask_OnALargeStore_ReadsOnlyItsBatchOfTheHeldIndexAsync() {
    const int HELD = 20_000;
    const int BATCH = 100;
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var origin = TrackedGuid.New().Value;
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1, '{TYPE}', NULL, nextval('wh_commit_seq'),
             NOW() - make_interval(secs => x.g), '{origin}'::uuid, CASE WHEN x.g % 2 = 0 THEN x.g END
      FROM (SELECT g, gen_random_uuid() AS s FROM generate_series(1, {HELD * 2}) AS g) x
      """);
    await _execAsync(conn, "ANALYZE wh_event_store");
    // A cursor part way through the history, as a walk that has run a while has.
    _ = await _store().AskAsync(HELD / 2, TimeSpan.FromMinutes(30), _t0, CancellationToken.None);

    var nodes = await _plansOfAsync(conn, $"SELECT * FROM wh_origin_position_ask({BATCH}, INTERVAL '30 minutes', NOW())");

    var reads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    Console.WriteLine($"#2544 ask: wh_event_store rows read {reads.Sum(n => n.RowsRead)}, blocks {reads.Sum(n => n.Blocks)}, "
      + $"over {HELD} held rows");
    await Assert.That(reads.Any(n => n.Type == "Seq Scan")).IsFalse().Because("the walk reads the held index, never the store");
    await Assert.That(reads.Sum(n => n.RowsRead)).IsLessThanOrEqualTo(BATCH + 10)
      .Because($"one batch reads its {BATCH} rows and the origin skip-scan one entry per origin, never the {HELD} held rows");
  }

  // ── the origin's answer ──────────────────────────────────────────────

  [Test]
  public async Task PositionsOf_AnswersStamped_Unstamped_Archived_AndUnknownAsync() {
    await using var conn = await _openAsync();
    var stamped = await _seedAsync(conn, null, null, minutesAgo: 10);
    var unstamped = await _seedAsync(conn, null, null, minutesAgo: 10);
    await _execAsync(conn, $"UPDATE wh_event_store SET commit_sequence = NULL WHERE event_id = '{unstamped}'");
    var received = await _seedAsync(conn, TrackedGuid.New().Value, 5, minutesAgo: 10);
    var archived = TrackedGuid.New().Value;
    var archivedNoSequence = TrackedGuid.New().Value;
    foreach (var (id, seq) in new[] { (archived, (long?)41), (archivedNoSequence, null) }) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        INSERT INTO wh_event_archive (event_id, stream_id, event_type, version, commit_sequence) VALUES (@id, @id, @t, 1, @seq)
        """;
      cmd.Parameters.AddWithValue("id", id);
      cmd.Parameters.AddWithValue("t", TYPE);
      cmd.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = (object?)seq ?? DBNull.Value });
      await cmd.ExecuteNonQueryAsync();
    }
    var never = TrackedGuid.New().Value;
    var stampedSequence = await _scalarAsync(conn, $"SELECT commit_sequence FROM wh_event_store WHERE event_id = '{stamped}'");

    var lookups = (await _store().PositionsOfAsync([stamped, unstamped, received, archived, archivedNoSequence, never, stamped],
      CancellationToken.None)).ToDictionary(l => l.EventId);

    await Assert.That(lookups.Count).IsEqualTo(6).Because("an id asked twice is answered once");
    await Assert.That(lookups[stamped]).IsEqualTo(new OriginPositionLookup(stamped, "positioned", stampedSequence));
    await Assert.That(lookups[unstamped]).IsEqualTo(new OriginPositionLookup(unstamped, "unstamped", null));
    await Assert.That(lookups[received].State).IsEqualTo("unknown")
      .Because("an event this service received from another is not its to place");
    await Assert.That(lookups[archived]).IsEqualTo(new OriginPositionLookup(archived, "archived", 41));
    await Assert.That(lookups[archivedNoSequence].State).IsEqualTo("unknown");
    await Assert.That(lookups[never].State).IsEqualTo("unknown");
  }

  // ── the fill ─────────────────────────────────────────────────────────

  [Test]
  public async Task FillPlan_ReturnsTheOriginsEventsOfTheStreamsTheFillTouches_OnlyAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var untouched = TrackedGuid.New().Value;
    var e1 = await _seedAsync(conn, origin, 10, minutesAgo: 10, stream: stream, version: 1);
    var e2 = await _seedAsync(conn, origin, null, minutesAgo: 10, stream: stream, version: 2);
    _ = await _seedAsync(conn, other, 3, minutesAgo: 10, stream: stream, version: 3);
    _ = await _seedAsync(conn, null, null, minutesAgo: 10, stream: stream, version: 4);
    _ = await _seedAsync(conn, origin, null, minutesAgo: 10, stream: untouched, version: 1);

    var rows = new List<(Guid Stream, Guid Event, long Version, long? Seq)>();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT stream_id, event_id, version, origin_commit_sequence FROM wh_origin_position_fill_plan(@o, @ids)";
      cmd.Parameters.AddWithValue("o", origin);
      cmd.Parameters.AddWithValue("ids", new[] { e2, e1, TrackedGuid.New().Value });
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
      }
    }

    await Assert.That(rows).IsEquivalentTo(new List<(Guid, Guid, long, long?)> { (stream, e1, 1, 10), (stream, e2, 2, null) },
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("only the origin's events of the streams holding a fillable named event: other origins keep their places");
    await using var none = conn.CreateCommand();
    none.CommandText = "SELECT count(*) FROM wh_origin_position_fill_plan(@o, @ids)";
    none.Parameters.AddWithValue("o", origin);
    none.Parameters.AddWithValue("ids", new[] { e1 });
    await Assert.That(Convert.ToInt64(await none.ExecuteScalarAsync(), CultureInfo.InvariantCulture)).IsEqualTo(0L)
      .Because("a named event that already has its position fills nothing, so nothing is locked or read");
  }

  [Test]
  public async Task Fill_FillsOnlyNullPositions_AndNeverOverwritesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var held = await _seedAsync(conn, origin, null, minutesAgo: 10);
    var positioned = await _seedAsync(conn, origin, 7, minutesAgo: 10);
    var fromOther = await _seedAsync(conn, TrackedGuid.New().Value, null, minutesAgo: 10);
    var zero = await _seedAsync(conn, origin, null, minutesAgo: 10);

    var result = await _store().FillAsync(new OriginPositionsAnswer {
      OriginServiceId = origin,
      Positions = [new OriginPosition(held, 21), new OriginPosition(positioned, 99), new OriginPosition(fromOther, 22),
        new OriginPosition(zero, 0)],
    }, _t0, CancellationToken.None);

    await Assert.That(result.Filled).IsEqualTo(1);
    await Assert.That(await _originSeqAsync(conn, held)).IsEqualTo(21L);
    await Assert.That(await _originSeqAsync(conn, positioned)).IsEqualTo(7L).Because("a position held is never overwritten");
    await Assert.That(await _originSeqAsync(conn, fromOther)).IsNull().Because("an origin fills only events received from it");
    await Assert.That(await _originSeqAsync(conn, zero)).IsNull().Because("zero is no position");
    await Assert.That(async () => await _fillAsync(conn, origin, [held], [], unknown: []))
      .Throws<PostgresException>().Because("ids and sequences pair by position");
  }

  [Test]
  public async Task Fill_RefoldsTheSealedEpochAFilledEventFallsIn_AndTheHeldFoldLetsItGoAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var ids = await _receiveAsync(conn, origin, stream, [2, null, 4, 5, 6, 7]);
    await _setAsync(conn, "integrity_epoch_width", "2", "integer");
    await _execAsync(conn, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    await _execAsync(conn, "SELECT close_digest_epochs(NOW(), 0, 1000)");
    var before = await _epochCountAsync(conn, origin, 1);
    var unfilledBefore = (await _store().UnfilledAsync(CancellationToken.None)).Single(u => u.OriginServiceId == origin).Unfilled;

    var result = await _store().FillAsync(new OriginPositionsAnswer { OriginServiceId = origin, Positions = [new(ids[1], 3)] },
      _t0, CancellationToken.None);

    await Assert.That(result.Filled).IsEqualTo(1);
    await Assert.That(before).IsEqualTo(1L).Because("the sealed epoch [2, 4) counted only position 2 while 3 was unplaced");
    await Assert.That(await _epochCountAsync(conn, origin, 1)).IsEqualTo(2L)
      .Because("the filled event now falls in the sealed epoch, which is refolded");
    await Assert.That(unfilledBefore).IsEqualTo(1L);
    await Assert.That((await _store().UnfilledAsync(CancellationToken.None)).Any(u => u.OriginServiceId == origin)).IsFalse()
      .Because("the held fold lets the event go: it no longer counts in the open window as held without a position");
  }

  [Test]
  public async Task Fill_RecordsUnknownAnswers_AndTheAskNeverReturnsThemAgainAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var unknown = await _seedAsync(conn, origin, null, minutesAgo: 10);
    var notYet = await _seedAsync(conn, origin, null, minutesAgo: 9);

    var result = await _store().FillAsync(new OriginPositionsAnswer {
      OriginServiceId = origin,
      Unknown = [unknown, TrackedGuid.New().Value],
      NotYetStamped = [notYet],
    }, _t0, CancellationToken.None);
    var again = await _store().FillAsync(new OriginPositionsAnswer { OriginServiceId = origin, Unknown = [unknown] },
      _t0, CancellationToken.None);
    var ask = await _store().AskAsync(10, TimeSpan.FromMinutes(30), _t0, CancellationToken.None);

    await Assert.That(result).IsEqualTo(new OriginPositionFillResult(0, 1, 0, 0));
    await Assert.That(again.UnknownRecorded).IsEqualTo(0).Because("recorded once");
    await Assert.That(ask!.EventIds).IsEquivalentTo([notYet])
      .Because("an event not stamped yet is asked again; an unknown one never is");
  }

  [Test]
  public async Task Fill_ThatChangesAStreamsOriginOrder_RewindsItsPerspectivesFromTheStartAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var reordered = TrackedGuid.New().Value;
    var kept = TrackedGuid.New().Value;
    // Ids ascend with creation: the unplaced event sorts between the two positioned ones, as the origin minted it.
    var r1 = await _seedAsync(conn, origin, 10, minutesAgo: 10, stream: reordered, version: 1);
    var r2 = await _seedAsync(conn, origin, null, minutesAgo: 10, stream: reordered, version: 2);
    _ = await _seedAsync(conn, origin, 30, minutesAgo: 10, stream: reordered, version: 3);
    var k1 = await _seedAsync(conn, origin, 10, minutesAgo: 10, stream: kept, version: 1);
    var k2 = await _seedAsync(conn, origin, null, minutesAgo: 10, stream: kept, version: 2);
    _ = await _seedAsync(conn, origin, 30, minutesAgo: 10, stream: kept, version: 3);
    foreach (var (stream, last) in new[] { (reordered, r2), (kept, k2) }) {
      await _execAsync(conn, $"""
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES ('{stream}', 'P1', '{last}', 2);
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES ('{stream}', '__collective__', '{last}', 2);
        INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version)
        VALUES ('{stream}', 'P1', '{last}', jsonb_build_object(), 2, 2);
        """);
    }

    var result = await _store().FillAsync(new OriginPositionsAnswer {
      OriginServiceId = origin,
      Positions = [new(r2, 40), new(k2, 20)],
    }, _t0, CancellationToken.None);

    await Assert.That(result).IsEqualTo(new OriginPositionFillResult(2, 0, 1, 1))
      .Because("position 40 moves the event past 30; position 20 leaves it where its id placed it");
    await Assert.That(await _scalarAsync(conn, $"SELECT status & {REWIND_REQUIRED} FROM wh_perspective_cursors WHERE stream_id = '{reordered}' AND perspective_name = 'P1'"))
      .IsEqualTo((long)REWIND_REQUIRED);
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = '{reordered}' AND perspective_name = 'P1' AND rewind_trigger_event_id = '{r1}'"))
      .IsEqualTo(1L).Because("the rewind replays from the stream's first event");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{reordered}' AND perspective_name = 'P1' AND event_id = '{r1}' AND processed_at IS NULL"))
      .IsEqualTo(1L).Because("the trigger is queued so the rewind runs");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = '{reordered}'"))
      .IsEqualTo(0L).Because("each snapshot held a fold in the old order");
    await Assert.That(await _scalarAsync(conn, $"SELECT status & {REWIND_REQUIRED} FROM wh_perspective_cursors WHERE stream_id = '{reordered}' AND perspective_name = '__collective__'"))
      .IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, $"SELECT status & {REWIND_REQUIRED} FROM wh_perspective_cursors WHERE stream_id = '{kept}' AND perspective_name = 'P1'"))
      .IsEqualTo(0L).Because("a fill that leaves the order as it was rewinds nothing");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = '{kept}'")).IsEqualTo(1L);
    await Assert.That(k1).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Unknown_IsForgotten_WhenItsEventIsDeletedOrPositionedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var deleted = await _seedAsync(conn, origin, null, minutesAgo: 10);
    var positioned = await _seedAsync(conn, origin, null, minutesAgo: 10);
    var stays = await _seedAsync(conn, origin, null, minutesAgo: 10);
    await _fillAsync(conn, origin, [], [], unknown: [deleted, positioned, stays]);

    await _execAsync(conn, $"DELETE FROM wh_event_store WHERE event_id = '{deleted}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET origin_commit_sequence = 9 WHERE event_id = '{positioned}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET created_at = created_at - INTERVAL '1 second' WHERE event_id = '{stays}'");

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_origin_position_unknown")).IsEqualTo(1L);
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_origin_position_unknown WHERE event_id = '{stays}'")).IsEqualTo(1L)
      .Because("a row leaves only with its event's place in the held set; any other write leaves it");
  }

  [Test]
  public async Task Unknown_IsForgotten_WhenTheEventStoreIsTruncatedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var held = await _seedAsync(conn, origin, null, minutesAgo: 10);
    await _fillAsync(conn, origin, [], [], unknown: [held]);
    _ = await _store().AskAsync(10, TimeSpan.FromMinutes(30), _t0, CancellationToken.None);

    await _execAsync(conn, "TRUNCATE wh_event_store CASCADE");

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_origin_position_unknown")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_origin_position_walk")).IsEqualTo(0L);
  }

  [Test]
  public async Task Unfilled_CountsTheEligibleHeldEventsPerOrigin_AndFallsAsTheyFillAsync() {
    await using var conn = await _openAsync();
    var a = TrackedGuid.New().Value;
    var b = TrackedGuid.New().Value;
    var a1 = await _seedAsync(conn, a, null, minutesAgo: 10);
    _ = await _seedAsync(conn, a, null, minutesAgo: 10);
    _ = await _seedAsync(conn, b, null, minutesAgo: 10);
    _ = await _seedAsync(conn, b, 3, minutesAgo: 10);

    var before = await _store().UnfilledAsync(CancellationToken.None);
    await _store().FillAsync(new OriginPositionsAnswer { OriginServiceId = a, Positions = [new(a1, 5)] }, _t0, CancellationToken.None);
    await _execAsync(conn, "SELECT wh_compact_unsequenced_digest_changes(100000)");
    var after = await _store().UnfilledAsync(CancellationToken.None);

    await Assert.That(before.ToDictionary(x => x.OriginServiceId, x => x.Unfilled))
      .IsEquivalentTo(new Dictionary<Guid, long> { [a] = 2, [b] = 1 });
    await Assert.That(after.ToDictionary(x => x.OriginServiceId, x => x.Unfilled))
      .IsEquivalentTo(new Dictionary<Guid, long> { [a] = 1, [b] = 1 })
      .Because("read from the maintained totals and their pending changes, folded or not");
  }

  // ── the step ─────────────────────────────────────────────────────────

  [Test]
  public async Task Step_AsksEachOriginInBoundedBatches_CountsAndLogsEachAsync() {
    await using var conn = await _openAsync();
    var audited = TrackedGuid.New().Value;
    var unaudited = TrackedGuid.New().Value;
    for (var i = 0; i < 3; i++) {
      _ = await _seedAsync(conn, audited, null, minutesAgo: 60 - i);
    }
    _ = await _seedAsync(conn, unaudited, null, minutesAgo: 10);
    var transport = new IntegrityWireTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(audited, "audited-svc", _t0, "audited.requests");
    await using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var requested = new List<long>();
    var unfilled = new List<long>();
    using var listener = _listen(meters, ("whizbang.stream_integrity.origin_positions_requested", requested),
      ("whizbang.stream_integrity.origin_positions_unfilled", unfilled));
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: meters.GetRequiredService<IMeterFactory>()));
    var options = new StreamIntegrityOptions { OriginPositionFill = { BatchSize = 2, MaxBatchesPerCycle = 3 } };
    await using var services = _services(options, transport, tracker, metrics);
    var logger = new ListLogger();
    var step = new OriginPositionFillStep(logger);

    var cycle = await step.FillAsync(services, CancellationToken.None);

    await Assert.That(cycle).IsEqualTo(new OriginPositionFillCycle(3, 3, "budget"))
      .Because("three batches: two of the audited origin (2 then 1) and one of the origin nothing audits, which is not sent");
    var sent = transport.Published.Where(p => p.EnvelopeType?.Contains(nameof(RequestOriginPositions), StringComparison.Ordinal) == true).ToList();
    await Assert.That(sent.Count).IsEqualTo(2);
    await Assert.That(sent.TrueForAll(p => p.Destination.Address == "audited.requests")).IsTrue()
      .Because("directed at the address the origin's checkpoint announced");
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("audited-svc", StringComparison.Ordinal)))
      .IsEqualTo(2).Because("one line per batch");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains(unaudited.ToString(), StringComparison.Ordinal))).IsTrue();
    listener.RecordObservableInstruments();
    await Assert.That(requested.Sum()).IsEqualTo(3L);
    await Assert.That(unfilled).IsEquivalentTo([3L, 1L]).Because("the unfilled count is published per origin each cycle");
    await Assert.That(step.Name).IsEqualTo("origin-position-fill");
    await Assert.That((await step.FillAsync(services, CancellationToken.None)).Status).IsEqualTo("caught-up")
      .Because("every lap ended: nothing is due until the pause passes");
    await step.RunAsync(services, CancellationToken.None);
  }

  [Test]
  public async Task Step_IsSkipped_WhenOffOrWithoutTheWire_AndItsArgumentsAreCheckedAsync() {
    await using var off = _services(new StreamIntegrityOptions { OriginPositionFill = { Enabled = false } }, null, null, null);
    var step = OriginPositionFillStep.FromServices(off);

    await Assert.That((await step.FillAsync(off, CancellationToken.None)).Status)
      .IsEqualTo("skipped (Whizbang:StreamIntegrity:OriginPositionFill:Enabled=false)");
    await using var noWire = _services(new StreamIntegrityOptions(), null, null, null);
    await Assert.That((await step.FillAsync(noWire, CancellationToken.None)).Status).StartsWith("skipped (no integrity wire");
    await using var bare = new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider();
    await Assert.That((await step.FillAsync(bare, CancellationToken.None)).Status).StartsWith("skipped (no integrity wire");
    await Assert.That(() => OriginPositionFillStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new OriginPositionFillStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new OriginPositionStore(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await step.FillAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _store().PositionsOfAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _store().FillAsync(null!, _t0, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStep_AndTheReceptorsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<OriginPositionFillStep>().Count()).IsEqualTo(1)
      .Because("turnkey: the fill runs in every maintenance cycle unless switched off");
    await Assert.That(services.Any(d => d.ImplementationType == typeof(OriginPositionReceptorRegistrar))).IsTrue();
  }

  [Test]
  public async Task Step_ABatchOfOnlyUnknownRows_SendsNothing_AndAnOriginWithNoAddressIsNotAskedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var unknown = await _seedAsync(conn, origin, null, minutesAgo: 10);
    await _fillAsync(conn, origin, [], [], unknown: [unknown]);
    var transport = new IntegrityWireTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", _t0, "origin.requests");
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "silent-svc", _t0);
    await using var services = _services(new StreamIntegrityOptions(), transport, tracker, metrics: null);

    var cycle = await new OriginPositionFillStep(NullLogger.Instance).FillAsync(services, CancellationToken.None);

    await Assert.That(cycle).IsEqualTo(new OriginPositionFillCycle(1, 0, "caught-up"))
      .Because("the batch read its row and advanced the walk, but the origin already answered that it cannot place it");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task AnswerReceptor_FillsAndCountsEachOutcome_AndAnswersWithoutADatabaseAreIgnoredAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var held = await _seedAsync(conn, origin, null, minutesAgo: 10);
    await using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var filled = new List<long>();
    var notFilled = new List<long>();
    using var listener = _listen(meters, ("whizbang.stream_integrity.origin_positions_filled", filled),
      ("whizbang.stream_integrity.origin_positions_not_filled", notFilled));
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: meters.GetRequiredService<IMeterFactory>()));
    await using var services = _services(new StreamIntegrityOptions(), null, null, metrics);
    var receptor = new OriginPositionsAnswerReceptor(services.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<OriginPositionsAnswerReceptor>.Instance);

    await receptor.HandleAsync(new OriginPositionsAnswer {
      OriginServiceId = origin,
      Positions = [new(held, 12)],
      NotYetStamped = [TrackedGuid.New().Value],
      Unknown = [TrackedGuid.New().Value, TrackedGuid.New().Value],
    });

    await Assert.That(await _originSeqAsync(conn, held)).IsEqualTo(12L);
    await receptor.HandleAsync(new OriginPositionsAnswer { OriginServiceId = origin, OriginServiceName = "origin-svc" });
    listener.RecordObservableInstruments();
    await Assert.That(filled.Sum()).IsEqualTo(1L);
    await Assert.That(notFilled.Sum()).IsEqualTo(3L).Because("one not stamped yet and two unknown, counted apart by answer");
    await using var bare = new ServiceCollection().BuildServiceProvider();
    await new OriginPositionsAnswerReceptor(bare.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<OriginPositionsAnswerReceptor>.Instance).HandleAsync(new OriginPositionsAnswer { OriginServiceId = origin });
    await new RequestOriginPositionsReceptor(bare.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RequestOriginPositionsReceptor>.Instance).HandleAsync(
        new RequestOriginPositions { RequesterService = "consumer-svc", Topic = "consumer.inbox", EventIds = [held] });
    await using var databaseOnly = new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider();
    await new RequestOriginPositionsReceptor(databaseOnly.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RequestOriginPositionsReceptor>.Instance).HandleAsync(
        new RequestOriginPositions { RequesterService = "consumer-svc", Topic = "consumer.inbox", EventIds = [held] });
    await Assert.That(async () => await receptor.HandleAsync(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new RequestOriginPositionsReceptor(bare.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RequestOriginPositionsReceptor>.Instance).HandleAsync(null!)).Throws<ArgumentNullException>();
    metrics.UpdateUnfilledOriginPositions(null);
    var unfilled = new List<long>();
    using var gauge = _listen(meters, ("whizbang.stream_integrity.origin_positions_unfilled", unfilled));
    gauge.RecordObservableInstruments();
    await Assert.That(unfilled).IsEmpty().Because("a null reading reports no series");
  }

  [Test]
  public async Task RequestReceptor_AnswersEachAskedEventInExactlyOneList_ToTheAskersAddressAsync() {
    await using var conn = await _openAsync();
    var stamped = await _seedAsync(conn, null, null, minutesAgo: 10);
    var unstamped = await _seedAsync(conn, null, null, minutesAgo: 10);
    await _execAsync(conn, $"UPDATE wh_event_store SET commit_sequence = NULL WHERE event_id = '{unstamped}'");
    var archived = TrackedGuid.New().Value;
    await _execAsync(conn, $"INSERT INTO wh_event_archive (event_id, stream_id, event_type, version, commit_sequence) VALUES ('{archived}', '{archived}', '{TYPE}', 1, 77)");
    var never = TrackedGuid.New().Value;
    var transport = new IntegrityWireTransport();
    var json = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    await using var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton<ITransport>(transport)
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(json))
      .AddScoped<IWorkCoordinator>(_ => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), json))
      .BuildServiceProvider();
    var receptor = new RequestOriginPositionsReceptor(services.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RequestOriginPositionsReceptor>.Instance);

    await receptor.HandleAsync(new RequestOriginPositions {
      RequesterService = "consumer-svc",
      Topic = "consumer.inbox",
      EventIds = [stamped, unstamped, archived, never],
      RequestId = TrackedGuid.New().Value,
    });

    var (envelope, destination, _) = transport.Published.Single();
    var answer = (OriginPositionsAnswer)JsonSerializer.Deserialize(
      ((Whizbang.Core.Observability.MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(), json.GetTypeInfo(typeof(OriginPositionsAnswer)))!;
    await Assert.That(destination.Address).IsEqualTo("consumer.inbox");
    await Assert.That(answer.Positions.Select(p => p.EventId).ToList()).IsEquivalentTo(new[] { stamped, archived }.ToList());
    await Assert.That(answer.Positions.Single(p => p.EventId == archived).CommitSequence).IsEqualTo(77L);
    await Assert.That(answer.NotYetStamped).IsEquivalentTo([unstamped]);
    await Assert.That(answer.Unknown).IsEquivalentTo([never]);
    await Assert.That(answer.OriginServiceName).IsNull().Because("a host with no service identity answers without a name");
  }

  [Test]
  public async Task TheStoreAndTheReport_ReachTheTablesThroughTheSearchPath_WhenNoSchemaIsNamedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    _ = await _seedAsync(conn, origin, null, minutesAgo: 10);
    var searchPath = new PostgresArchiveConnection(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, Schema: null);

    var unfilled = await new OriginPositionStore(searchPath).UnfilledAsync(CancellationToken.None);
    await using var services = new ServiceCollection().AddSingleton(searchPath).BuildServiceProvider();
    var unaudited = await new UnauditedSealReportStep(NullLogger.Instance).ReportAsync(services, CancellationToken.None);

    await Assert.That(unfilled.Single().OriginServiceId).IsEqualTo(origin).Because("the Dapper driver names no schema");
    await Assert.That(unaudited).IsEmpty();
  }

  // ── fixture ──────────────────────────────────────────────────────────

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private OriginPositionStore _store() => new(_connection());

  private ServiceProvider _services(StreamIntegrityOptions options, IntegrityWireTransport? transport, IntegrityGapTracker? tracker,
      StreamIntegrityMetrics? metrics) {
    var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton(Options.Create(options))
      .AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)));
    if (transport is not null) {
      services.AddSingleton<ITransport>(transport);
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
      services.AddSingleton<IServiceInstanceProvider>(new IntegrityWireInstance("consumer-svc"));
      var consumerOptions = new TransportConsumerOptions();
      consumerOptions.Destinations.Add(new TransportDestination("consumer.inbox"));
      services.AddSingleton(consumerOptions);
    }
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  private static MeterListener _listen(ServiceProvider meters, params (string Name, List<long> Into)[] instruments) {
    var factory = meters.GetRequiredService<IMeterFactory>();
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Scope == factory && instruments.Any(i => i.Name == instrument.Name)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
      instruments.First(i => i.Name == instrument.Name).Into.Add(value));
    listener.Start();
    return listener;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Writes one event straight to the store (the held-fold triggers fire; no digest is folded). Ids are minted in
  /// order, so a later call's event sorts after an earlier one's.</summary>
  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, Guid? origin, long? originSequence, int minutesAgo,
      Guid? stream = null, int version = 1) {
    var id = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@id, @sid, @sid, 'TestAggregate', @v, @t, NULL, nextval('wh_commit_seq'), NOW() - make_interval(mins => @ago), @o, @seq)
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("sid", stream ?? id);
    cmd.Parameters.AddWithValue("v", version);
    cmd.Parameters.AddWithValue("t", TYPE);
    cmd.Parameters.AddWithValue("ago", minutesAgo);
    cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Uuid) { Value = (object?)origin ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = (object?)originSequence ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task _fillAsync(NpgsqlConnection conn, Guid origin, Guid[] ids, long[] sequences, Guid[] unknown) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM wh_fill_origin_positions(@o, @ids, @seqs, @unknown, @rewind, NOW())";
    cmd.Parameters.AddWithValue("o", origin);
    cmd.Parameters.AddWithValue(nameof(ids), ids);
    cmd.Parameters.AddWithValue("seqs", sequences);
    cmd.Parameters.AddWithValue(nameof(unknown), unknown);
    cmd.Parameters.AddWithValue("rewind", Array.Empty<Guid>());
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // Drained.
    }
  }

  /// <summary>Stores events received from an origin through the inbox, each at the given origin position (null: none).</summary>
  private static async Task<Guid[]> _receiveAsync(NpgsqlConnection conn, Guid origin, Guid stream, long?[] sequences) {
    Guid[] ids = [.. Enumerable.Range(0, sequences.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    for (var i = 0; i < ids.Length; i++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        SELECT * FROM store_inbox_messages(
          jsonb_build_array(jsonb_build_object(
            'MessageId', @id::text, 'HandlerName', 'FillHandler', 'MessageType', @type::text,
            'Envelope', jsonb_build_object('MessageId', @id::text, 'Payload', '{}'::jsonb, 'Hops', '[]'::jsonb),
            'EnvelopeType', 'env', 'StreamId', @sid::text, 'IsEvent', true, 'Flags', 0,
            'Metadata', '{}'::jsonb, 'SourceServiceId', @o::text, 'SourceCommitSequence', @seq)),
          @inst, NOW() + INTERVAL '5 minutes', NOW(), 100)
        """;
      cmd.Parameters.AddWithValue("id", ids[i]);
      cmd.Parameters.AddWithValue("type", TYPE);
      cmd.Parameters.AddWithValue("sid", stream);
      cmd.Parameters.AddWithValue("o", origin);
      cmd.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = (object?)sequences[i] ?? DBNull.Value });
      cmd.Parameters.AddWithValue("inst", (Guid)TrackedGuid.New());
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // Drained.
      }
    }
    await _execAsync(conn,
      $"UPDATE wh_inbox_state SET instance_id = '{instance}', lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY('{{{string.Join(',', ids)}}}'::uuid[])");
    await _execAsync(conn, $"SELECT _emit_event_store_chain_for_inbox('{instance}', NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10)");
    await _execAsync(conn, $"DELETE FROM wh_inbox_state WHERE message_id = ANY('{{{string.Join(',', ids)}}}'::uuid[])");
    await _execAsync(conn, $"DELETE FROM wh_inbox WHERE message_id = ANY('{{{string.Join(',', ids)}}}'::uuid[])");
    await _execAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _execAsync(conn, $"UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY('{{{string.Join(',', ids)}}}'::uuid[])");
    return ids;
  }

  private static Task _setAsync(NpgsqlConnection conn, string key, string value, string type) =>
    _execAsync(conn, $"""
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('{key}', '{value}', '{type}', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);

  private static async Task<long?> _originSeqAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT origin_commit_sequence FROM wh_event_store WHERE event_id = '{id}'";
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
  }

  private static Task<long> _epochCountAsync(NpgsqlConnection conn, Guid origin, long epoch) =>
    _scalarAsync(conn, $"SELECT COALESCE(SUM(event_count), 0) FROM wh_digest_epochs WHERE origin_service_id = '{origin}' AND epoch_id = {epoch}");

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    do {
      while (await reader.ReadAsync()) {
        // Drained: run for its effect, every row read so a failure surfaces.
      }
    } while (await reader.NextResultAsync());
  }

  private sealed record PlanNode(string Type, string? Relation, long RowsRead, long Blocks);

  private static async Task<IReadOnlyList<PlanNode>> _plansOfAsync(NpgsqlConnection conn, string sql) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_buffers = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await _execAsync(conn, sql);
    conn.Notice -= Collect;
    await _execAsync(conn, "SET auto_explain.log_min_duration = -1");
    var nodes = new List<PlanNode>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      _flatten(document.RootElement.GetProperty("Plan"), nodes);
    }
    return nodes;
  }

  private static void _flatten(JsonElement node, List<PlanNode> nodes) {
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
    var loops = node.GetProperty("Actual Loops").GetDouble();
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      (long)((node.GetProperty("Actual Rows").GetDouble()
        + (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0)) * loops),
      (long)((node.TryGetProperty("Shared Hit Blocks", out var hit) ? hit.GetDouble() : 0)
        + (node.TryGetProperty("Shared Read Blocks", out var read) ? read.GetDouble() : 0))));
  }
}
