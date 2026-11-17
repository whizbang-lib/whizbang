// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Rules;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2362, across two services over two real stores (<see cref="TwoServiceHarness"/>): integrity runs for the pair only
/// while both apply the same eligibility rules to the event types they share. Each test loses one event on the way to
/// the consumer, so a pair that proceeds finds and repairs it, and a halted pair leaves it alone and says why.
/// </summary>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class TwoServicePairGuardTests : EFCoreTestBase {
  private const string SHARED = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string SHARED_NAME = "TwoService.Contracts.OrderPlaced";
  private const string NOT_SHARED_NAME = "TwoService.Contracts.AuditNoted";
  private const string PAYLOAD = """{"k":"v"}""";

  /// <summary>A service's declared rules, by type name; settable, so a test can align them mid-run.</summary>
  private sealed class Rules(params TypeRules[] types) : IRuleFingerprintSource {
    public Dictionary<string, TypeRules> Types { get; } = types.ToDictionary(t => t.TypeName, StringComparer.Ordinal);

    public RuleSet RulesFor(IEnumerable<string>? eventTypes) {
      var covered = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var name in eventTypes?.Select(RuleTypeName.Of) ?? Types.Keys) {
        if (Types.TryGetValue(name, out var rules)) {
          covered[name] = rules.Hash();
        }
      }
      return new RuleSet(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION, covered);
    }
  }

  private static Rules _sourced() => new(new TypeRules(SHARED_NAME), new TypeRules(NOT_SHARED_NAME));

  /// <summary>The origin emits two events and the consumer receives only the first: the second is the loss.</summary>
  private static async Task<Guid> _loseOneAsync(TwoServiceHarness h) {
    var ids = await h.EmitAsync(
      new TwoServiceEmit((Guid)TrackedGuid.New(), SHARED, 0, null, PAYLOAD),
      new TwoServiceEmit((Guid)TrackedGuid.New(), SHARED, 0, null, PAYLOAD));
    await h.DeliverAsync([ids[0]]);
    return ids[1];
  }

  [Test]
  public async Task TheSameRules_ThePairProceeds_AndTheLossIsRepairedAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      originRules: _sourced(), consumerRules: _sourced());
    var lost = await _loseOneAsync(h);

    await h.AuditAsync(TwoServiceAudit.TypesWindowed, SHARED);

    await Assert.That(h.Tally().RepairRequests).IsGreaterThan(0).Because("the pair agrees, so the audit compares");
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsTrue();
    await Assert.That(h.ConsumerGuard.Pairs.All(p => p.HaltedSince is null)).IsTrue();
    await Assert.That(h.OriginGuard.Pairs.All(p => p.HaltedSince is null)).IsTrue();
  }

  [Test]
  public async Task ASharedTypesRulesDiffer_ThePairIsHaltedOnBothSides_WithTheReason_AndResumesOnceTheyMatchAsync() {
    var consumerRules = new Rules(new TypeRules(SHARED_NAME) { Ephemeral = "WhenConsumed;PersistedRow" });
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      originRules: _sourced(), consumerRules: consumerRules);
    var lost = await _loseOneAsync(h);

    for (var cycle = 1; cycle <= 2; cycle++) {
      await h.AuditAsync(TwoServiceAudit.TypesWindowed, SHARED);
      await h.AuditAsync(TwoServiceAudit.Streams, SHARED);
    }

    await Assert.That(h.Tally()).IsEqualTo(new TwoServiceTally(0, 0, 0, 0))
      .Because("a halted pair compares, reports, requests and redelivers nothing");
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsFalse();
    var origin = h.OriginGuard.Pairs.Single(p => p.Peer == TwoServiceHarness.CONSUMER_NAME);
    await Assert.That(origin.Side).IsEqualTo(IntegrityPairSide.Origin);
    await Assert.That(origin.Verdict.Reason).IsEqualTo(RulePairHaltReason.TypeRules);
    await Assert.That(origin.Verdict.DifferingTypes).IsEquivalentTo([SHARED_NAME]);
    var consumer = h.ConsumerGuard.Pairs.Single(p => p.Peer == TwoServiceHarness.ORIGIN_NAME);
    await Assert.That(consumer.Verdict.Reason).IsEqualTo(RulePairHaltReason.TypeRules)
      .Because("the origin answers a halted pair with its own rules, so the consumer names the same types");
    await Assert.That(consumer.Verdict.DifferingTypes).IsEquivalentTo([SHARED_NAME]);
    await Assert.That(consumer.HaltedSince.HasValue).IsTrue();

    // The rules are aligned (the rollout finished): the next audit compares again, with nothing else done.
    consumerRules.Types[SHARED_NAME] = new TypeRules(SHARED_NAME);
    await h.AuditAsync(TwoServiceAudit.TypesWindowed, SHARED);

    await Assert.That(h.Tally().RepairRequests).IsGreaterThan(0);
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsTrue().Because("the pair resumed on its own and repaired the loss");
    await Assert.That(h.ConsumerGuard.Pairs.Single(p => p.Peer == TwoServiceHarness.ORIGIN_NAME).HaltedSince).IsNull();
  }

  [Test]
  public async Task AConsumerWithNoFingerprint_IsNotAnswered_AndThePairIsHaltedAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      originRules: _sourced(), consumerRules: _sourced());
    var lost = await _loseOneAsync(h);

    await h.AuditAsync(TwoServiceAudit.TypesWindowed, stampRules: false, SHARED);
    await h.AuditAsync(TwoServiceAudit.Streams, stampRules: false, SHARED);

    await Assert.That(h.Tally()).IsEqualTo(new TwoServiceTally(0, 0, 0, 0))
      .Because("a consumer on an older release may fold another way, so the origin answers nothing");
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsFalse();
    await Assert.That(h.OriginGuard.Pairs.Single().Verdict.Reason).IsEqualTo(RulePairHaltReason.NoFingerprint);
  }

  [Test]
  public async Task AnOriginWithNoFingerprint_IsNotCompared_AndThePairIsHaltedAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      originRules: _sourced(), consumerRules: _sourced());
    h.OriginPredatesRuleFingerprint = true;
    var lost = await _loseOneAsync(h);

    await h.AuditAsync(TwoServiceAudit.TypesWindowed, SHARED);
    await h.AuditAsync(TwoServiceAudit.Streams, SHARED);

    await Assert.That(h.Tally()).IsEqualTo(new TwoServiceTally(0, 0, 0, 0))
      .Because("an origin on an older release may fold another way, so the consumer compares nothing");
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsFalse();
    await Assert.That(h.ConsumerGuard.Pairs.Single().Verdict.Reason).IsEqualTo(RulePairHaltReason.NoFingerprint);
  }

  /// <summary>Owner decision Q3: the fingerprint covers only the types the pair shares.</summary>
  [Test]
  public async Task RulesDifferOnlyForATypeThePairDoesNotShare_ThePairProceedsAsync() {
    var originRules = new Rules(new TypeRules(SHARED_NAME), new TypeRules(NOT_SHARED_NAME) { Terminal = true });
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [],
      consumedEventTypes: [SHARED], originRules: originRules, consumerRules: _sourced());
    var lost = await _loseOneAsync(h);

    await h.RunConsumerAuditAsync(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      AuditSettleWindowMinutes = 0,
      RepairRequestBackoffSeconds = 0,
      RepairDrainEnabled = false,
      MaxAskSkipMinutes = 0,
    });

    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsTrue()
      .Because("the audit worker stamps the consumed types only, and they agree: the pair proceeds and repairs");
    await Assert.That(originRules.RulesFor(null).CompareWith(_sourced().RulesFor(null)).Reason)
      .IsEqualTo(RulePairHaltReason.TypeRules).Because("positive control: over every type the two would differ");
    await Assert.That(h.OriginGuard.Pairs.Single().HaltedSince).IsNull();
  }
}
