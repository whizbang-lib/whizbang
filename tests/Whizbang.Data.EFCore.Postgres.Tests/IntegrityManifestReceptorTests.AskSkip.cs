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
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2349: both halves of not asking an unchanged origin. The consumer records how each type-level compare of the window
/// above its seal ended (a full match is remembered, a mismatch forgets), and the origin, finding nothing settled at or
/// above the ask's start, computes nothing and says so in its reply.
/// </summary>
/// <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
public partial class IntegrityManifestReceptorTests {

  [Test]
  public async Task ManifestReceptor_ACleanCompleteWindow_IsRememberedAsAFullMatchAsync() {
    var coordinator = new AuditCoordinator {
      Seal = 100,
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)], ComputedThrough = 300 },
    };
    var sp = _provider(coordinator, new CaptureTransport(), tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    });

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(300L);
    await Assert.That(coordinator.CompareOutcomes.Single()).IsEqualTo((coordinator.OriginId, true))
      .Because("the window above the seal matched in full, so the next audit may skip the ask while nothing moves");
  }

  [Test]
  public async Task ManifestReceptor_AWindowThatDisagrees_ForgetsTheMatchAsync() {
    var coordinator = new AuditCoordinator {
      Seal = 100,
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 41, 42, 4)], ComputedThrough = 300 },
    };
    var sp = _provider(coordinator, new CaptureTransport(), tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 51, 52, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    });

    await Assert.That(coordinator.SealAdvancedTo).IsNull();
    await Assert.That(coordinator.CompareOutcomes.Single()).IsEqualTo((coordinator.OriginId, false))
      .Because("a window under repair must be asked again next audit, not skipped until the bound");
  }

  [Test]
  public async Task ManifestReceptor_NothingSettledAbove_FoldsNothing_AndIsRememberedAsAFullMatchAsync() {
    var coordinator = new AuditCoordinator { Seal = 300 };
    var sp = _provider(coordinator, new CaptureTransport(), tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [], ManifestLevel.Types) with {
      SinceSequence = 300,
      ComputedThrough = 300,
      ChunkCount = 1,
      NothingSettledAbove = true,
    });

    await Assert.That(coordinator.WindowedSinceSeen).IsNull()
      .Because("the origin vouched there is nothing above the seal; folding an empty window here proves nothing");
    await Assert.That(coordinator.CompareOutcomes.Single()).IsEqualTo((coordinator.OriginId, true))
      .Because("this is how an origin already sealed to its head establishes the memo the next audit skips on");
  }

  [Test]
  public async Task ManifestReceptor_NothingSettledAbove_AWindowStartingAboveTheSeal_IsNotRememberedAsync() {
    var coordinator = new AuditCoordinator { Seal = 100 };
    var sp = _provider(coordinator, new CaptureTransport(), tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [], ManifestLevel.Types) with {
      SinceSequence = 300,
      ComputedThrough = 300,
      ChunkCount = 1,
      NothingSettledAbove = true,
    });

    await Assert.That(coordinator.CompareOutcomes).IsEmpty()
      .Because("the history between the seal and the window's start was not verified, so nothing above the seal is known to match");
  }

  [Test]
  public async Task RequestReceptor_NothingSettledAboveTheStart_SaysSoInTheReply_AndCountsItAsync() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    long unchanged = 0;
    using var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        // This metrics instance only: another test's instance publishes an instrument of the same name.
        if (ReferenceEquals(instrument, metrics.ManifestAnswersUnchanged.Instrument)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => System.Threading.Interlocked.Add(ref unchanged, value));
    listener.Start();
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 100 },
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, metrics: metrics);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RuleFingerprint = IntegrityPairGuard.Fallback.Stamp(null),
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Types,
      Windowed = true,
      SinceSequence = 100,
    });

    var answer = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(answer.NothingSettledAbove).IsTrue()
      .Because("silence cannot be told from a lost answer; the reply lets the asker remember an unchanged origin");
    await Assert.That(answer.Digests).IsEmpty();
    await Assert.That(answer.SinceSequence).IsEqualTo(100L);
    await Assert.That(answer.ComputedThrough).IsEqualTo(100L).Because("no coverage is claimed past the start");
    await Assert.That(answer.ChunkCount).IsEqualTo(1);
    await Assert.That(answer.OriginGeneration).IsEqualTo(coordinator.OriginGeneration);
    listener.RecordObservableInstruments();
    await Assert.That(System.Threading.Interlocked.Read(ref unchanged)).IsEqualTo(1L)
      .Because("the saving is visible: each ask answered without computing is counted");
  }

  [Test]
  public async Task RequestReceptor_NothingSettledAboveAStreamLevelStart_StaysSilentAsync() {
    var coordinator = new AuditCoordinator {
      WindowedStreamResult = new WindowedDigestResult { Digests = [], ComputedThrough = 100 },
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RuleFingerprint = IntegrityPairGuard.Fallback.Stamp(null),
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Streams,
      Windowed = true,
      SinceSequence = 100,
    });

    await Assert.That(transport.Published).IsEmpty()
      .Because("a drill-down page follows a type that disagreed; the skip memo is the type level's, so a page says nothing new");
  }
}
