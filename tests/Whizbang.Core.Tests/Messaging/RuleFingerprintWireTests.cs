// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2362: the rule fingerprint rides every integrity message. It round-trips through the wire JSON on each of the four
/// carriers, and a payload from a release that predates it reads back with none.
/// </summary>
[Category("Messaging")]
public class RuleFingerprintWireTests {
  private static readonly RuleSet _rules = new(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION,
    new Dictionary<string, string>(StringComparer.Ordinal) { ["Contracts.B"] = "bbbb", ["Contracts.A"] = "aaaa" });

  private static T _roundTrip<T>(T value) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var json = JsonSerializer.Serialize(value, options.GetTypeInfo(typeof(T)));
    return (T)JsonSerializer.Deserialize(json, options.GetTypeInfo(typeof(T)))!;
  }

  [Test]
  public async Task From_OrdersTheTypes_AndToRuleSetReadsTheSameRulesBackAsync() {
    var wire = RuleFingerprint.From(_rules);

    await Assert.That(wire.Types.Select(t => t.Type)).IsEquivalentTo(["Contracts.A", "Contracts.B"]);
    await Assert.That(wire.Types[0].Type).IsEqualTo("Contracts.A");
    await Assert.That(wire.Fingerprint).IsEqualTo(_rules.Fingerprint());
    await Assert.That(wire.ToRuleSet().CompareWith(_rules).IsAgreed).IsTrue();
    await Assert.That(wire.ToRuleSet().Fingerprint()).IsEqualTo(_rules.Fingerprint());
    await Assert.That(() => RuleFingerprint.From(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task EveryCarrier_RoundTripsItsFingerprintAsync() {
    var fingerprint = RuleFingerprint.From(_rules);
    var id = Guid.Parse("0193e7a0-0000-7000-8000-000000000001");

    var request = _roundTrip(new RequestIntegrityManifest { RequesterService = "c", Topic = "t", RuleFingerprint = fingerprint });
    var manifest = _roundTrip(new IntegrityManifest {
      ManifestStreamId = id,
      OriginServiceId = id,
      OriginServiceName = "o",
      RuleFingerprint = fingerprint,
    });
    var checkpoint = _roundTrip(new IntegrityCheckpoint {
      CheckpointStreamId = id,
      OriginServiceId = id,
      OriginServiceName = "o",
      FromCommitSequence = 0,
      ToCommitSequence = 1,
      RuleFingerprint = fingerprint,
    });
    var redelivery = _roundTrip(new RequestRedeliveryCommand { RequesterService = "c", Topic = "t", RuleFingerprint = fingerprint });

    foreach (var carried in new[] { request.RuleFingerprint, manifest.RuleFingerprint, checkpoint.RuleFingerprint, redelivery.RuleFingerprint }) {
      await Assert.That(carried).IsNotNull();
      await Assert.That(carried!.Fingerprint).IsEqualTo(fingerprint.Fingerprint);
      await Assert.That(carried.ToRuleSet().CompareWith(_rules).IsAgreed).IsTrue();
    }
  }

  [Test]
  public async Task APayloadFromAnOlderRelease_ReadsBackWithNoFingerprintAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var request = (RequestIntegrityManifest)JsonSerializer.Deserialize("""{"RequesterService":"c","Topic":"t"}""",
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;

    await Assert.That(request.RuleFingerprint).IsNull();
    await Assert.That(_rules.CompareWith(request.RuleFingerprint?.ToRuleSet()).Reason).IsEqualTo(RulePairHaltReason.NoFingerprint);
  }
}
