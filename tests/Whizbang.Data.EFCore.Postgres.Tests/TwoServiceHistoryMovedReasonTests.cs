// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Rules;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2365, owner decision, across two services over two real stores: an origin's answer says why its history moved since
/// the generation the consumer knows, and the consumer counts the seal it lowers by that reason. A history the origin
/// rewrote after its eligibility rules changed is a rule change; one rewritten under the same rules is a rewrite.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class TwoServiceHistoryMovedReasonTests : EFCoreTestBase {
  private const string SHARED = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string SHARED_NAME = "TwoService.Contracts.OrderPlaced";
  private const string PAYLOAD = """{"k":"v"}""";

  /// <summary>A service's declared rules, settable so a test can change them as a rollout would.</summary>
  private sealed class Rules : IRuleFingerprintSource {
    public TypeRules Shared { get; set; } = new(SHARED_NAME);

    public RuleSet RulesFor(IEnumerable<string>? eventTypes) =>
      new(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION,
        new Dictionary<string, string>(StringComparer.Ordinal) { [SHARED_NAME] = Shared.Hash() });
  }

  private static StreamIntegrityOptions _audit() => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    AuditSettleWindowMinutes = 0,
    RepairRequestBackoffSeconds = 0,
    RepairDrainEnabled = false,
    MaxAskSkipMinutes = 0,
  };

  private static Dictionary<string, long> _lowered(StreamIntegrityMetrics metrics) {
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, metrics.SealLowered.Instrument)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      foreach (var tag in tags) {
        if (tag.Key == "reason") {
          counts[(string)tag.Value!] = value;
        }
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return counts;
  }

  [Test]
  public async Task AHistoryMovedByARuleChange_AndOneMovedByARewrite_AreCountedApartAsync() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var (originRules, consumerRules) = (new Rules(), new Rules());
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [], consumedEventTypes: [SHARED],
      originRules: originRules, consumerRules: consumerRules, consumerMetrics: metrics);
    var ids = await h.EmitAsync(new TwoServiceEmit((Guid)TrackedGuid.New(), SHARED, 0, null, PAYLOAD));
    await h.DeliverAsync([ids[0]]);
    await h.RunConsumerAuditAsync(_audit());   // the consumer records the origin's generation
    await h.RewriteOriginHistoryAsync();       // a generation the consumer has seen under these rules
    await h.RunConsumerAuditAsync(_audit());

    // The rollout: both services take new rules for the shared type, and the origin refolds its history under them.
    originRules.Shared = new TypeRules(SHARED_NAME) { Terminal = true };
    consumerRules.Shared = new TypeRules(SHARED_NAME) { Terminal = true };
    await h.RewriteOriginHistoryAsync();
    await h.RunConsumerAuditAsync(_audit());
    var afterTheRuleChange = _lowered(metrics);

    // An ordinary rewrite under the same rules.
    await h.RewriteOriginHistoryAsync();
    await h.RunConsumerAuditAsync(_audit());
    var afterTheRewrite = _lowered(metrics);

    await Assert.That(afterTheRuleChange.GetValueOrDefault("rule_change")).IsEqualTo(1L)
      .Because("the rules changed after the generation the consumer knew");
    await Assert.That(afterTheRewrite.GetValueOrDefault("rule_change")).IsEqualTo(1L);
    await Assert.That(afterTheRewrite.GetValueOrDefault("rewrite")).IsEqualTo(
      afterTheRuleChange.GetValueOrDefault("rewrite") + 1).Because("the same rules, history rewritten: a rewrite");
    await Assert.That(afterTheRewrite.GetValueOrDefault("unknown")).IsEqualTo(0L)
      .Because("an origin of this release always says why");
  }
}
