// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The integrity bundle's fixes, across two services (<see cref="TwoServiceHarness"/>): an origin and a consumer over two
/// real Postgres stores with the real integrity receptors between them. Each test runs several audit cycles and asserts
/// that the pair converges (nothing requested, reported or redelivered once the consumer holds what it should) beside a
/// positive control that the audit did run and does find a genuine loss.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class TwoServiceIntegrityConvergenceTests : EFCoreTestBase {
  /// <summary>A superseding type the catalog registers, so a redelivered child's key is read from its payload.</summary>
  private static readonly string _superseding = TypeNameFormatter.Format(typeof(SupersedingProbeEmbedded));
  private const string DURABLE = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string PAYLOAD = """{"k":"v"}""";

  /// <summary>
  /// #2350: the consumer holds the newest event of a key; a redelivery carries an older copy of the key. The copy is never
  /// stored and never queued for a perspective, and audit cycles after it find the pair equal: no further resend.
  /// </summary>
  [Test]
  public async Task AnOlderRedeliveredCopy_IsNeverStoredOrApplied_AndThePairConvergesWithNoFurtherResendAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions,
      new Dictionary<string, EventFlags> { [typeof(SupersedingProbeEmbedded).FullName!] = EventFlags.Superseding });
    await TwoServiceHarness.ExecuteAsync(h.Consumer, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', 'TwoService.ProfilePerspective', 'consumer-svc', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, ("t", _superseding));
    var stream = (Guid)TrackedGuid.New();
    var payload = JsonSerializer.Serialize(new SupersedingProbeEmbedded { DocumentId = stream, Model = "k1" },
      JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(SupersedingProbeEmbedded)));
    var ids = await h.EmitAsync(
      new TwoServiceEmit(stream, _superseding, TwoServiceHarness.FLAG_SUPERSEDING, "k1", payload),
      new TwoServiceEmit(stream, _superseding, TwoServiceHarness.FLAG_SUPERSEDING, "k1", payload));
    var (older, newest) = (ids[0], ids[1]);
    await h.DeliverAsync([newest]);

    await h.OperatorRedeliverAsync([stream], [_superseding]);

    Console.WriteLine($"DIAG older {older} newest {newest} redelivered {string.Join(",", h.Redelivered)}");
    Console.WriteLine("DIAG " + await TwoServiceHarness.DescribeAsync(h.Consumer, stream));
    await Assert.That(h.Redelivered).Contains(older).Because("positive control: the redelivery carried the older copy");
    await Assert.That(await h.ConsumerHoldsAsync(older)).IsFalse().Because("an older copy than the one held is never stored");
    await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer,
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @id", ("id", older))).IsEqualTo(0L)
      .Because("a copy never stored is never queued for a perspective");
    var settled = h.Tally();
    var held = await TwoServiceHarness.FingerprintAsync(h.Consumer, [stream]);

    for (var cycle = 1; cycle <= 3; cycle++) {
      await h.AuditAsync(TwoServiceAudit.TypesTable, _superseding);
      await h.AuditAsync(TwoServiceAudit.Streams, _superseding);
      await h.AuditAsync(TwoServiceAudit.TypesWindowed, _superseding);
      await Assert.That(h.Tally()).IsEqualTo(settled)
        .Because($"cycle {cycle}: the pair is equal, so nothing is requested, reported or resent");
      await Assert.That(await TwoServiceHarness.FingerprintAsync(h.Consumer, [stream])).IsEqualTo(held);
    }
  }

  /// <summary>
  /// #2351: the consumer holds events of the origin without the origin's commit sequence (held without a sequence).
  /// Repeated windowed audits, with history growing between them, answer correctly: nothing requested while the consumer
  /// holds everything, and a genuine loss found. The held change log is folded each cycle by the maintenance compaction,
  /// so it holds only the cycle's new rows, never the history.
  /// </summary>
  [Test]
  public async Task RepeatedAudits_WithEventsHeldWithoutASequence_AnswerCorrectly_AndTheChangeLogStaysFlatAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, []);
    const int PER_CYCLE = 5;
    var logBeforeCompaction = new List<long>();

    for (var cycle = 1; cycle <= 4; cycle++) {
      var ids = await h.EmitAsync([.. Enumerable.Range(0, PER_CYCLE)
        .Select(_ => new TwoServiceEmit((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD))]);
      await Assert.That((await h.DeliverAsync(ids, withSequence: false)).Count).IsEqualTo(PER_CYCLE);
      logBeforeCompaction.Add(await TwoServiceHarness.CountAsync(h.Consumer, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes"));

      await h.AuditAsync(TwoServiceAudit.TypesWindowed, DURABLE);
      await h.AuditAsync(TwoServiceAudit.TypesTable, DURABLE);

      await Assert.That(h.Tally()).IsEqualTo(new TwoServiceTally(0, 0, 0, 0))
        .Because($"cycle {cycle}: the consumer holds every event, with or without the origin's sequence");
      _ = await TwoServiceHarness.CountAsync(h.Consumer, "SELECT wh_compact_unsequenced_digest_changes(1000)");
      await Assert.That(await TwoServiceHarness.CountAsync(h.Consumer, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes"))
        .IsEqualTo(0L).Because($"cycle {cycle}: the compaction folds the log");
    }
    Console.WriteLine($"change log before each compaction: {string.Join(",", logBeforeCompaction)}");
    await Assert.That(logBeforeCompaction.Distinct().Count()).IsEqualTo(1)
      .Because("the log holds one cycle's changes, the same every cycle, however much history is held");

    // Positive control: an event the consumer never received is found and repaired.
    var lost = await h.EmitAsync(new TwoServiceEmit((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD));
    await h.AuditAsync(TwoServiceAudit.TypesWindowed, DURABLE);
    await Assert.That(h.Tally().RepairRequests).IsGreaterThan(0).Because("the audit finds the genuine loss");
    await Assert.That(await h.ConsumerHoldsAsync(lost[0])).IsTrue().Because("and the redelivery restores it");
  }
}
