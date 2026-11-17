// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Rules;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2362: both receptors of the manifest exchange ask the pair guard first. The origin answers a requester whose rules
/// differ with its own rules and no digests, and a requester that sent none with nothing; the consumer compares nothing
/// from an answer without the origin's rules or one the origin marked halted.
/// </summary>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
public partial class IntegrityManifestReceptorTests {
  private static readonly RuleFingerprint _otherRules = RuleFingerprint.From(new RuleSet(
    RuleSemantics.VERSION + 1, RuleSemantics.ELIGIBILITY_SQL_VERSION, new System.Collections.Generic.Dictionary<string, string>()));

  [Test]
  public async Task RequestReceptor_ARequesterWithOtherRules_IsAnsweredWithThisSidesRulesAndNoDigestsAsync() {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)], ComputedThrough = 300 },
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RuleFingerprint = _otherRules,
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Types,
      Windowed = true,
      WindowNumber = 2,
    });

    var answer = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(answer.PairHalted).IsTrue();
    await Assert.That(answer.Digests).IsEmpty().Because("a halted pair compares nothing, so nothing is computed for it");
    await Assert.That(answer.RuleFingerprint).IsNotNull();
    await Assert.That(answer.RuleFingerprint!.SemanticsVersion).IsEqualTo(RuleSemantics.VERSION);
    await Assert.That(answer.WindowNumber).IsEqualTo(2);
    await Assert.That(answer.Level).IsEqualTo(ManifestLevel.Types);
  }

  [Test]
  public async Task RequestReceptor_ARequesterWithNoRules_IsNotAnsweredAsync() {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)], ComputedThrough = 300 },
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RuleFingerprint = null,
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Types,
      Windowed = true,
    });

    await Assert.That(transport.Published).IsEmpty().Because("a requester on an older release would not read the rules");
  }

  [Test]
  public async Task ManifestReceptor_AnAnswerWithoutTheOriginsRules_OrMarkedHalted_ComparesNothingAsync() {
    var coordinator = new AuditCoordinator {
      Seal = 100,
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 41, 42, 4)], ComputedThrough = 300 },
    };
    var sp = _provider(coordinator, new CaptureTransport(), tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    var divergent = _manifest(coordinator, [_typeDigest("Contracts.TypeX", 51, 52, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };

    await receptor.HandleAsync(divergent with { RuleFingerprint = null });
    await receptor.HandleAsync(divergent with { PairHalted = true });
    await receptor.HandleAsync(divergent with { Digests = [], PairHalted = true });

    await Assert.That(coordinator.CompareOutcomes).IsEmpty()
      .Because("the window disagrees, but a halted pair is not compared: its digests may be folded another way");
    await Assert.That(coordinator.SealAdvancedTo).IsNull();

    await receptor.HandleAsync(divergent);
    await Assert.That(coordinator.CompareOutcomes.Single()).IsEqualTo((coordinator.OriginId, false))
      .Because("positive control: the same answer with the origin's rules is compared and disagrees");
  }
}
