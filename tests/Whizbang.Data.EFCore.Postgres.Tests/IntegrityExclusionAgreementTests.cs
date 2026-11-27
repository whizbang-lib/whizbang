// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;
using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2588 across two services (<see cref="TwoServiceHarness"/>): an event the consumer excluded from integrity never shows
/// as a mismatch against its origin, a repair request, a report, a held window or an unknown answer, whichever way the
/// pair compares (windowed and table type roll-ups, the stream drill-down, the full stream level), and whatever the
/// origin knows of it: it holds it, it never wrote it, or the consumer has not asked yet. The origin is told nothing.
/// Each scenario has a positive control: the same history without the mark, or with the flag alone (no mark, so nothing
/// is removed from the origin's answer), diverges.
/// </summary>
/// <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityExclusionAdjustment.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/663_AnOperatorCanExcludeReceivedEventsFromIntegrity.sql</code-under-test>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class IntegrityExclusionAgreementTests : EFCoreTestBase {
  /// <summary>When the exclusions API stamps marks, the log and requests: a fixed instant, never the host clock.</summary>
  private static readonly FakeTimeProvider _clock = new(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

  private const string TYPE = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string PAYLOAD = """{"k":"v"}""";
  private const string REASON = "unresolvable after a defect";
  private const string OPERATOR = "operator-a";
  private const int EXCLUDED_FLAG = (int)EventFlags.IntegrityExcluded;

  /// <summary>
  /// When a hand-made fold is stamped: on the harness's own timeline, never the database's <c>NOW()</c>. The consumer's
  /// settle check reads the host clock, so a database clock a few seconds ahead of it made the fold look in flight, the
  /// bucket was skipped, and the controls saw nothing (and the agreement tests agreed without comparing).
  /// </summary>
  private const string FOLDED_AT = "TIMESTAMPTZ '2025-06-01 12:00:00+00'";

  private static TwoServiceEmit _event(Guid stream) => new(stream, TYPE, 0, null, PAYLOAD);

  // ── the origin holds the excluded event ──

  [Test]
  [Arguments(nameof(TwoServiceAudit.TypesWindowed))]
  [Arguments(nameof(TwoServiceAudit.TypesTable))]
  [Arguments(nameof(TwoServiceAudit.Streams))]
  public async Task AnExcludedEventTheOriginHolds_IsRemovedFromItsAnswer_SoNoAuditDivergesAsync(string auditName) {
    var audit = Enum.Parse<TwoServiceAudit>(auditName);
    await using var h = await _pairAsync();
    var stream = TrackedGuid.New().Value;
    var emptied = TrackedGuid.New().Value;
    var untouched = TrackedGuid.New().Value;
    // One event excluded from a stream of three, the only event of a second stream, and a third stream left alone.
    var ids = await h.EmitAsync(_event(stream), _event(stream), _event(stream), _event(emptied), _event(untouched));
    await h.DeliverAsync(ids);
    await h.CheckpointAsync();

    var result = await _exclusions(h).ExcludeAsync(new IntegrityExclusionRequest {
      Reason = REASON,
      RequestedBy = OPERATOR,
      EventIds = [ids[1], ids[3]],
    });
    await h.AuditAsync(audit, TYPE);
    await h.AuditAsync(audit, TYPE);

    await Assert.That(result.Outcomes.Single()).IsEqualTo(new IntegrityExclusionOutcome(h.OriginId, "marked", 2));
    await Assert.That(await _markStateAsync(h, ids[1])).IsEqualTo(1).Because("the consumer holds it at the origin's sequence");
    await _assertAgreedAsync(h, audit);
  }

  [Test]
  [Arguments(nameof(TwoServiceAudit.TypesWindowed))]
  [Arguments(nameof(TwoServiceAudit.TypesTable))]
  [Arguments(nameof(TwoServiceAudit.Streams))]
  public async Task PositiveControl_TheFlagWithoutItsMark_DivergesAsync(string auditName) {
    var audit = Enum.Parse<TwoServiceAudit>(auditName);
    await using var h = await _pairAsync();
    var stream = TrackedGuid.New().Value;
    var ids = await h.EmitAsync(_event(stream), _event(stream), _event(stream));
    await h.DeliverAsync(ids);
    await h.CheckpointAsync();

    // The consumer leaves the event out of its own folds, and nothing removes it from the origin's answer.
    await TwoServiceHarness.ExecuteAsync(h.Consumer,
      $"UPDATE wh_event_store SET flags = flags | {EXCLUDED_FLAG} WHERE event_id = @id", ("id", ids[1]));
    await TwoServiceHarness.ExecuteAsync(h.Consumer, "DELETE FROM wh_stream_digests WHERE stream_id = @s", ("s", stream));
    await TwoServiceHarness.ExecuteAsync(h.Consumer, $"""
      SELECT _wh_fold_stream_digests(array_agg(origin_service_id), array_agg(COALESCE(scope ->> 't', '')), array_agg(event_type),
        array_agg(stream_id), array_agg(event_id), 'fold', {FOLDED_AT})
      FROM wh_event_store WHERE stream_id = @s AND flags & 256 = 0
      """, ("s", stream));
    await h.AuditAsync(audit, TYPE);

    var tally = h.Tally();
    await Assert.That(tally.RepairRequests + tally.Reports).IsGreaterThan(0)
      .Because("without the mark the origin's answer still counts the event, so the pair diverges: the adjustment is what agrees");
  }

  // ── the origin never wrote it (the #2573 shape) ──

  [Test]
  [Arguments(nameof(TwoServiceAudit.TypesWindowed))]
  [Arguments(nameof(TwoServiceAudit.TypesTable))]
  [Arguments(nameof(TwoServiceAudit.Streams))]
  public async Task AnExcludedEventTheOriginDoesNotKnow_NeverDiverges_AndIsNeverAskedAgainAsync(string auditName) {
    var audit = Enum.Parse<TwoServiceAudit>(auditName);
    await using var h = await _pairAsync();
    var stream = TrackedGuid.New().Value;
    await h.DeliverAsync(await h.EmitAsync(_event(stream), _event(stream)));
    var ghost = await _ghostAsync(h, stream);
    await h.CheckpointAsync();
    _ = await h.RunConsumerPositionFillAsync();
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer, "SELECT count(*) FROM wh_origin_position_unknown"))
      .IsEqualTo(1L).Because("precondition: the origin answered that it never wrote the event");

    _ = await _exclusions(h).ExcludeAsync(new IntegrityExclusionRequest { Reason = REASON, RequestedBy = OPERATOR, EventIds = [ghost] });
    var asked = await h.RunConsumerPositionFillAsync();
    await h.AuditAsync(audit, TYPE);
    await h.AuditAsync(audit, TYPE);

    await Assert.That(await _markStateAsync(h, ghost)).IsEqualTo(2);
    await Assert.That(asked.Requested).IsEqualTo(0L).Because("an excluded event is not asked for again");
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer, "SELECT count(*) FROM wh_origin_position_unknown"))
      .IsEqualTo(0L).Because("its unknown answer is resolved by the mark");
    await _assertAgreedAsync(h, audit);
  }

  [Test]
  public async Task PositiveControl_AnEventTheOriginDoesNotKnow_WithoutAMark_IsReportedAsAnExtraAsync() {
    await using var h = await _pairAsync();
    var stream = TrackedGuid.New().Value;
    await h.DeliverAsync(await h.EmitAsync(_event(stream), _event(stream)));
    _ = await _ghostAsync(h, stream);
    await h.CheckpointAsync();

    await h.AuditAsync(TwoServiceAudit.Streams, TYPE);

    await Assert.That(h.Tally().Reports).IsGreaterThan(0).Because("the consumer holds an event its origin never wrote");
  }

  // ── a mark the origin has not placed yet ──

  [Test]
  [Arguments(nameof(TwoServiceAudit.TypesWindowed))]
  [Arguments(nameof(TwoServiceAudit.TypesTable))]
  [Arguments(nameof(TwoServiceAudit.Streams))]
  public async Task APendingMark_LeavesItsBucketUndecided_ThenTheAnswerSettlesIt_AndThePairAgreesAsync(string auditName) {
    var audit = Enum.Parse<TwoServiceAudit>(auditName);
    await using var h = await _pairAsync();
    var stream = TrackedGuid.New().Value;
    var ids = await h.EmitAsync(_event(stream), _event(stream), _event(stream));
    await h.DeliverAsync([ids[0], ids[2]]);
    await h.DeliverAsync([ids[1]], withSequence: false);
    await h.CheckpointAsync();

    _ = await _exclusions(h).ExcludeAsync(new IntegrityExclusionRequest { Reason = REASON, RequestedBy = OPERATOR, EventIds = [ids[1]] });
    await Assert.That(await _markStateAsync(h, ids[1])).IsEqualTo(0).Because("precondition: its place at the origin is not known");
    await h.AuditAsync(audit, TYPE);
    var whilePending = h.Tally();
    var sealWhilePending = await h.SealAsync();

    _ = await h.RunConsumerPositionFillAsync();
    await h.AuditAsync(audit, TYPE);
    await h.AuditAsync(audit, TYPE);

    await Assert.That(whilePending.RepairRequests + whilePending.Reports).IsEqualTo(0)
      .Because("a bucket holding a pending mark is undecided: compared, never repaired");
    await Assert.That(await _markStateAsync(h, ids[1])).IsEqualTo(1).Because("the origin's answer placed it");
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer,
      "SELECT count(*) FROM wh_event_store WHERE event_id = @id AND origin_commit_sequence IS NULL", ("id", ids[1])))
      .IsEqualTo(1L).Because("an excluded event's position is not filled: a fill can reorder the stream and rewind perspectives");
    if (audit == TwoServiceAudit.TypesWindowed) {
      await Assert.That(sealWhilePending).IsEqualTo(0L).Because("an undecided window is never sealed");
    }
    await _assertAgreedAsync(h, audit);
  }

  // ── unmark ──

  [Test]
  public async Task Unexcluding_ResolvesTheEventAgain_FromScratchAsync() {
    await using var h = await _pairAsync();
    var stream = TrackedGuid.New().Value;
    await h.DeliverAsync(await h.EmitAsync(_event(stream), _event(stream)));
    var ghost = await _ghostAsync(h, stream);
    await h.CheckpointAsync();
    var exclusions = _exclusions(h);
    _ = await exclusions.ExcludeAsync(new IntegrityExclusionRequest { Reason = REASON, RequestedBy = OPERATOR, EventIds = [ghost] });
    await h.AuditAsync(TwoServiceAudit.Streams, TYPE);
    await Assert.That(h.Tally().Reports).IsEqualTo(0).Because("precondition: excluded, the pair agrees");

    var undone = await exclusions.UnexcludeAsync(new IntegrityUnexclusionRequest { Reason = "the fix shipped", RequestedBy = OPERATOR, EventIds = [ghost] });
    await h.AuditAsync(TwoServiceAudit.Streams, TYPE);

    await Assert.That(undone.Outcomes.Single().Outcome).IsEqualTo("unmarked");
    await Assert.That(h.Tally().Reports).IsGreaterThan(0).Because("unmarked, the event the origin never wrote is an extra again");
  }

  // ── helpers ──

  private async Task<TwoServiceHarness> _pairAsync() =>
    await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [], [TYPE]);

  private static PostgresIntegrityExclusions _exclusions(TwoServiceHarness h) =>
    new(new PostgresArchiveConnection(async ct => {
      var connection = new NpgsqlConnection(h.Consumer.ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public"), new StreamIntegrityOptions(), _clock, TwoServiceHarness.CONSUMER_NAME,
      NullLogger<PostgresIntegrityExclusions>.Instance);

  /// <summary>An event the consumer holds under the origin that the origin never wrote, without a position, folded into
  /// the origin's lane: the reaction shape #2573 left behind.</summary>
  private static async Task<Guid> _ghostAsync(TwoServiceHarness h, Guid stream) {
    var id = TrackedGuid.New().Value;
    await TwoServiceHarness.ExecuteAsync(h.Consumer, $"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@id, @s, @s, 'TwoService', @t,
              (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @s),
              nextval('wh_commit_seq'), 0, NOW() - INTERVAL '1 hour', @o, NULL);
      SELECT _wh_fold_stream_digests(ARRAY[@o::uuid], ARRAY[''], ARRAY[@t::text], ARRAY[@s::uuid], ARRAY[@id::uuid], 'fold', {FOLDED_AT});
      """, ("id", id), ("s", stream), ("t", TYPE), ("o", h.OriginId));
    return id;
  }

  private static async Task<long> _markStateAsync(TwoServiceHarness h, Guid eventId) =>
    await TwoServiceHarness.CountAsync(h.Consumer,
      "SELECT COALESCE((SELECT origin_state FROM wh_integrity_exclusions WHERE event_id = @id), -1)", ("id", eventId));

  /// <summary>No repair, no report, no redelivery, no ledger row; and a windowed audit sealed the window.</summary>
  private static async Task _assertAgreedAsync(TwoServiceHarness h, TwoServiceAudit audit) {
    var tally = h.Tally();
    await Assert.That(tally.RepairRequests).IsEqualTo(0).Because("an excluded event is never repaired");
    await Assert.That(tally.Reports).IsEqualTo(0).Because("an excluded event never shows as a divergence");
    await Assert.That(tally.Redelivered).IsEqualTo(0);
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer, "SELECT count(*) FROM wh_integrity_ledger")).IsEqualTo(0L)
      .Because("no bucket is held as divergent");
    if (audit == TwoServiceAudit.TypesWindowed) {
      await Assert.That(await h.SealAsync()).IsGreaterThan(0L).Because("the window that holds the excluded event is sealed");
    }
  }
}
