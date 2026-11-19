// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2207: a type-level comparison that proves a (tenant, type) complete heals that type's ledger row on the type lane
/// (the empty stream), so a deficit recorded under an older rule clears once the type compares equal. A match is proof
/// only when both sides were counted over the whole history: a full-history answer, or a window that starts at or below
/// the seal (everything beneath it was verified) and was not cut short by the origin's window cap.
/// </summary>
public partial class IntegrityManifestReceptorTests {
  private const string LANE_TYPE = "Contracts.TypeX";

  private static (AuditCoordinator Coordinator, IntegrityRepairLedger Ledger, IntegrityManifestReceptor Receptor, Func<long> Heals)
      _typeLaneFixture() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    long heals = 0;
    var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        // This metrics instance only: another test's instance publishes an instrument of the same name.
        if (ReferenceEquals(instrument, metrics.BucketHealSeconds)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref heals));
    listener.Start();
    var coordinator = new AuditCoordinator();
    var ledger = new IntegrityRepairLedger();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, new CaptureTransport(),
      new StreamIntegrityOptions { PublishReportEvents = false }, tracker: tracker, ledger: ledger, metrics: metrics);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    return (coordinator, ledger, receptor, () => Interlocked.Read(ref heals));
  }

  private static async Task _seedTypeLaneAsync(IntegrityRepairLedger ledger, Guid origin, string eventType = LANE_TYPE) =>
    _ = await ledger.TryBeginReportAsync(
      new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", eventType, Guid.Empty), 41, 42, 0, 0, _fixedNow, TimeSpan.FromMinutes(60));

  // A re-report of the same signature inside the cooldown is refused only for a row the ledger still holds.
  private static async Task<bool> _hasTypeLaneAsync(IntegrityRepairLedger ledger, Guid origin, string eventType = LANE_TYPE) =>
    !await ledger.TryBeginReportAsync(
      new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", eventType, Guid.Empty), 41, 42, 0, 0, _fixedNow, TimeSpan.FromMinutes(60));

  [Test]
  public async Task TypeLevel_AFullHistoryMatch_HealsTheTypeLaneRow_AndRecordsTheHealAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    coordinator.ReceivedTypeDigests = [_typeDigest(LANE_TYPE, 41, 42, 5)];

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(ledger.Count).IsEqualTo(0)
      .Because("the whole history of the type compares equal, so the deficit recorded on its type lane is gone");
    await Assert.That(heals()).IsEqualTo(1L).Because("a type-lane heal is counted like every other heal");
  }

  [Test]
  public async Task TypeLevel_AWindowAtTheSeal_ThatReachedTheSettledHead_HealsTheTypeLaneRowAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    coordinator.Seal = 100;
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types)));

    await Assert.That(ledger.Count).IsEqualTo(0)
      .Because("everything beneath the seal was verified and the window covers the rest, so the whole type is proven");
    await Assert.That(heals()).IsEqualTo(1L);
  }

  [Test]
  public async Task TypeLevel_AMatchingWindowAboveTheSeal_LeavesTheTypeLaneRowAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    coordinator.Seal = 50;   // the history between the seal and the window's start was not verified
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types)));

    await Assert.That(await _hasTypeLaneAsync(ledger, coordinator.OriginId)).IsTrue()
      .Because("a window that starts above the seal proves only itself; the deficit may lie in the history before it");
    await Assert.That(heals()).IsEqualTo(0L);
  }

  [Test]
  public async Task TypeLevel_AMatchingWindowTheOriginCapped_LeavesTheTypeLaneRowAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    coordinator.Seal = 100;
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types))
      with { WindowCapped = true });

    await Assert.That(await _hasTypeLaneAsync(ledger, coordinator.OriginId)).IsTrue()
      .Because("a capped window stops short of the settled head, so the deficit may lie in the history after it");
    await Assert.That(heals()).IsEqualTo(0L);
  }

  [Test]
  public async Task TypeLevel_AnUndecidedType_LeavesTheTypeLaneRowAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    coordinator.Seal = 100;
    // Events held without the origin's sequence fill the count but not the fold: the window cannot decide.
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 0x1, 0x1, 3)], ComputedThrough = 300 };
    coordinator.UnsequencedDigests = [_typeDigest(LANE_TYPE, 0x40, 0x80, 2)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 0x7, 0x7, 5)], ManifestLevel.Types)));

    await Assert.That(await _hasTypeLaneAsync(ledger, coordinator.OriginId)).IsTrue()
      .Because("an undecided type is not proven complete, so its type lane is not healed");
    await Assert.That(heals()).IsEqualTo(0L);
  }

  [Test]
  public async Task TypeLevel_OneTypeMatchesAndAnotherDiverges_HealsOnlyTheMatchingTypesLaneAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    await _seedTypeLaneAsync(ledger, coordinator.OriginId, "Contracts.TypeY");
    coordinator.ReceivedTypeDigests = [_typeDigest(LANE_TYPE, 41, 42, 5), _typeDigest("Contracts.TypeY", 1, 2, 3)];

    await receptor.HandleAsync(_manifest(coordinator,
      [_typeDigest(LANE_TYPE, 41, 42, 5), _typeDigest("Contracts.TypeY", 7, 8, 9)], ManifestLevel.Types));

    await Assert.That(await _hasTypeLaneAsync(ledger, coordinator.OriginId)).IsFalse()
      .Because("the matching type heals even though another type of the same answer diverges");
    await Assert.That(await _hasTypeLaneAsync(ledger, coordinator.OriginId, "Contracts.TypeY")).IsTrue()
      .Because("the diverging type is not proven, so its type lane stays");
    await Assert.That(heals()).IsEqualTo(1L);
  }

  // No metrics registered, and a window from an asker that never stamped its start (read as 0, so at any seal): the
  // proven type still heals, with no histogram to record on.
  [Test]
  public async Task TypeLevel_AnUnstampedWindowStartWithNoMetrics_StillHealsTheTypeLaneRowAsync() {
    var coordinator = new AuditCoordinator { Seal = 0 };
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, new CaptureTransport(), new StreamIntegrityOptions { PublishReportEvents = false }, ledger: ledger);
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types)
      with { ComputedThrough = 300, ChunkCount = 1 });

    await Assert.That(ledger.Count).IsEqualTo(0);
  }
}
