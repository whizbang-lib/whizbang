// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Rules;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2362 with #2363, across two services over two real stores (<see cref="TwoServiceHarness"/>): the pair guard and
/// cuts that carry their digest, together. The consumer purges one stream it holds and loses one event of another on the
/// way. A pair that runs the same rules compares, repairs the loss and leaves the purged stream purged, because the
/// purge carried its digest. A pair whose consumer runs the rule semantics from before cuts carried (a rolling deploy in
/// progress) is halted: nothing is compared or redelivered, so the purge is never read as a loss, and once both run the
/// same rules the pair resumes on its own with the same outcome as the first.
/// </summary>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
/// <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class TwoServiceCutUnderPairGuardTests : EFCoreTestBase {
  private const string SHARED = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string SHARED_NAME = "TwoService.Contracts.OrderPlaced";
  private const string PAYLOAD = """{"k":"v"}""";

  /// <summary>A service's rules for the one shared type, at a rule-semantics version a test can change mid-run.</summary>
  private sealed class Rules(int semanticsVersion) : IRuleFingerprintSource {
    public int SemanticsVersion { get; set; } = semanticsVersion;

    public RuleSet RulesFor(IEnumerable<string>? eventTypes) {
      var covered = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var name in eventTypes?.Select(RuleTypeName.Of) ?? [SHARED_NAME]) {
        if (name == SHARED_NAME) {
          covered[name] = new TypeRules(SHARED_NAME).Hash();
        }
      }
      return new RuleSet(SemanticsVersion, RuleSemantics.ELIGIBILITY_SQL_VERSION, covered);
    }
  }

  /// <summary>The events of the stream the consumer purges, and the one event lost on the way.</summary>
  private sealed record Seeded(Guid[] Purged, Guid Lost);

  /// <summary>
  /// The origin emits three events on one stream and two on another; the consumer receives all but the last, then purges
  /// the first stream (a cut that carries its digest).
  /// </summary>
  private static async Task<Seeded> _purgeOneStreamAndLoseOneEventAsync(TwoServiceHarness h) {
    var purgedStream = (Guid)TrackedGuid.New();
    var lossStream = (Guid)TrackedGuid.New();
    var purged = await h.EmitAsync(
      new TwoServiceEmit(purgedStream, SHARED, 0, null, PAYLOAD),
      new TwoServiceEmit(purgedStream, SHARED, 0, null, PAYLOAD),
      new TwoServiceEmit(purgedStream, SHARED, 0, null, PAYLOAD));
    var other = await h.EmitAsync(
      new TwoServiceEmit(lossStream, SHARED, 0, null, PAYLOAD),
      new TwoServiceEmit(lossStream, SHARED, 0, null, PAYLOAD));
    await h.DeliverAsync([.. purged, other[0]]);
    await TwoServiceHarness.ExecuteAsync(h.Consumer,
      "SELECT * FROM wh_purge_streams(@s, false, @p, 0, 'two-service', 'two-service')",
      ("s", new[] { purgedStream }), ("p", (Guid)TrackedGuid.New()));
    foreach (var id in purged) {
      await Assert.That(await h.ConsumerHoldsAsync(id)).IsFalse().Because("precondition: the purge removed the stream");
    }
    return new Seeded(purged, other[1]);
  }

  private static async Task _auditBothLevelsAsync(TwoServiceHarness h) {
    await h.AuditAsync(TwoServiceAudit.TypesWindowed, SHARED);
    await h.AuditAsync(TwoServiceAudit.Streams, SHARED);
  }

  /// <summary>Both sides of an agreed pair: the loss repaired once, the purged stream still purged.</summary>
  private static async Task _assertRepairedAndStillPurgedAsync(TwoServiceHarness h, Seeded seeded) {
    await Assert.That(await h.ConsumerHoldsAsync(seeded.Lost)).IsTrue()
      .Because("positive control: the pair compares, so the genuine loss beside the purge is found and repaired");
    foreach (var id in seeded.Purged) {
      await Assert.That(await h.ConsumerHoldsAsync(id)).IsFalse()
        .Because("the purge carried its digest, so the compare reads no loss in the purged stream and nothing returns");
    }
    await Assert.That(h.Tally().Restored).IsEqualTo(1).Because("only the lost event is stored again");
    await Assert.That(h.ConsumerGuard.Pairs.All(p => p.HaltedSince is null)).IsTrue();
    await Assert.That(h.OriginGuard.Pairs.All(p => p.HaltedSince is null)).IsTrue();
  }

  [Test]
  public async Task TheSameRules_AConsumerPurge_IsNotReadAsALoss_AndALossBesideItIsRepairedAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      originRules: new Rules(RuleSemantics.VERSION), consumerRules: new Rules(RuleSemantics.VERSION));
    var seeded = await _purgeOneStreamAndLoseOneEventAsync(h);

    for (var cycle = 1; cycle <= 2; cycle++) {
      await _auditBothLevelsAsync(h);
    }

    await _assertRepairedAndStillPurgedAsync(h, seeded);
  }

  [Test]
  public async Task AConsumerOnTheSemanticsBeforeCutsCarried_HaltsThePair_AndAPurgeThere_IsNeverRepairedAsync() {
    var consumerRules = new Rules(RuleSemantics.VERSION - 1);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      originRules: new Rules(RuleSemantics.VERSION), consumerRules: consumerRules);
    var seeded = await _purgeOneStreamAndLoseOneEventAsync(h);

    for (var cycle = 1; cycle <= 2; cycle++) {
      await _auditBothLevelsAsync(h);
    }

    await Assert.That(h.Tally()).IsEqualTo(new TwoServiceTally(0, 0, 0, 0))
      .Because("a pair that counts two ways compares, reports, requests and redelivers nothing");
    await Assert.That(await h.ConsumerHoldsAsync(seeded.Lost)).IsFalse();
    var origin = h.OriginGuard.Pairs.Single(p => p.Peer == TwoServiceHarness.CONSUMER_NAME);
    await Assert.That(origin.Verdict.Reason).IsEqualTo(RulePairHaltReason.SemanticsVersion);
    var consumer = h.ConsumerGuard.Pairs.Single(p => p.Peer == TwoServiceHarness.ORIGIN_NAME);
    await Assert.That(consumer.Verdict.Reason).IsEqualTo(RulePairHaltReason.SemanticsVersion)
      .Because("the origin answers a halted pair with its own rules, so the consumer halts for the same reason");

    // The rollout finished: the consumer runs the same semantics, and the next audits compare with nothing else done.
    consumerRules.SemanticsVersion = RuleSemantics.VERSION;
    await _auditBothLevelsAsync(h);

    await _assertRepairedAndStillPurgedAsync(h, seeded);
  }
}
