// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2262: an ask names the origin generation the asker last recorded, the origin answers how far its history was
/// rewritten since then, and the asker's guard lowers the seal only that far. Every ask a consumer sends carries the
/// generation, or a new generation would fall back to the old reset to 0.
/// </summary>
public partial class IntegrityManifestReceptorTests {
  private static IntegrityManifestRequestReceptor _requestReceptor(AuditCoordinator coordinator, CaptureTransport transport) {
    var sp = _provider(coordinator, transport);
    return new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);
  }

  [Test]
  public async Task RequestReceptor_AnAskThatNamesAGeneration_CarriesHowFarHistoryWasRewrittenSinceAsync() {
    var coordinator = new AuditCoordinator {
      OriginGeneration = 12,
      OwnDigests = [_digest(TrackedGuid.New().Value, 11, 21, 2)],
      HistoryRewrite = new IntegrityHistoryRewrite(9, 300),
    };
    var transport = new CaptureTransport();

    await _requestReceptor(coordinator, transport).HandleAsync(
      new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox", KnownOriginGeneration = 9 });

    await Assert.That(coordinator.HistoryRewriteSinceSeen).IsEqualTo(9L);
    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.OriginGeneration).IsEqualTo(12L);
    await Assert.That(manifest.RewrittenSinceGeneration).IsEqualTo(9L)
      .Because("the floor is only good for an asker that recorded this generation or a later one");
    await Assert.That(manifest.RewrittenFrom).IsEqualTo(300L);
  }

  [Test]
  public async Task RequestReceptor_AnAskThatNamesNoGeneration_VouchesForNothingAsync() {
    var coordinator = new AuditCoordinator {
      OriginGeneration = 12,
      OwnDigests = [_digest(TrackedGuid.New().Value, 11, 21, 2)],
      HistoryRewrite = new IntegrityHistoryRewrite(9, 300),
    };
    var transport = new CaptureTransport();

    await _requestReceptor(coordinator, transport).HandleAsync(
      new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(coordinator.HistoryRewriteSinceSeen).IsNull();
    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.RewrittenSinceGeneration).IsNull();
    await Assert.That(manifest.RewrittenFrom).IsNull();
  }

  [Test]
  public async Task RequestReceptor_AnOriginThatCannotVouch_LeavesTheFloorOffAsync() {
    var coordinator = new AuditCoordinator {
      OriginGeneration = 12,
      OwnDigests = [_digest(TrackedGuid.New().Value, 11, 21, 2)],
    };
    var transport = new CaptureTransport();

    await _requestReceptor(coordinator, transport).HandleAsync(
      new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox", KnownOriginGeneration = 3 });

    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.RewrittenSinceGeneration).IsNull()
      .Because("an asker further behind than the origin's rewrite log must fall back to the reset");
    await Assert.That(manifest.RewrittenFrom).IsNull();
  }

  [Test]
  public async Task ManifestReceptor_HandsTheRewriteFloorToTheGuardAsync() {
    var coordinator = new AuditCoordinator { SealGenerationCoherent = false };
    var receptor = new IntegrityManifestReceptor(
      _provider(coordinator, new CaptureTransport()).GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(TrackedGuid.New().Value, 11, 21, 2)]) with {
      OriginGeneration = 12,
      RewrittenSinceGeneration = 9,
      RewrittenFrom = 300,
    });

    await Assert.That(coordinator.GenerationGuardSeen).IsEqualTo(12L);
    await Assert.That(coordinator.GuardRewriteSeen).IsEqualTo(new IntegrityHistoryRewrite(9, 300));
  }

  [Test]
  public async Task ManifestReceptor_AManifestWithoutAFloor_HandsTheGuardNoneAsync() {
    var coordinator = new AuditCoordinator { SealGenerationCoherent = false };
    var receptor = new IntegrityManifestReceptor(
      _provider(coordinator, new CaptureTransport()).GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(TrackedGuid.New().Value, 11, 21, 2)]) with {
      OriginGeneration = 12,
      RewrittenFrom = 300,
    });

    await Assert.That(coordinator.GenerationGuardSeen).IsEqualTo(12L);
    await Assert.That(coordinator.GuardRewriteSeen).IsNull()
      .Because("a floor without the generation it counts from cannot be placed against the recorded generation");
  }

  [Test]
  public async Task ManifestReceptor_TheDrillDownAsk_NamesTheGenerationTheAnswerCarriedAsync() {
    var coordinator = new AuditCoordinator { ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)] };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 1 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types)
      with { OriginGeneration = 13 });

    await Assert.That(_requests(transport).Single().KnownOriginGeneration).IsEqualTo(13L);
  }

  [Test]
  public async Task ManifestReceptor_TheCursorFollowAsk_NamesTheGenerationTheAnswerCarriedAsync() {
    var stream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator { ReceivedDigests = [_digest(stream, 41, 42, 5)] };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 41, 42, 5)])) with {
      ResumeAfterStreamId = TrackedGuid.New().Value,
      OriginGeneration = 14,
    });

    await Assert.That(_requests(transport).Single().KnownOriginGeneration).IsEqualTo(14L);
  }

  [Test]
  public async Task ManifestReceptor_TheNextWindowAsk_NamesTheGenerationTheAnswerCarriedAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)])
      with { OriginGeneration = 15 });

    await Assert.That(_requests(transport).Single().KnownOriginGeneration).IsEqualTo(15L);
  }
}
