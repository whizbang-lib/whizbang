// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phantom heal (follow-up to #2207): a type-level comparison that proves a (tenant, type) complete over the whole
/// history proves every stream of it complete, so every per-stream ledger row of that (origin, tenant, type) is a phantom
/// and is forgotten with the type lane, each heal recorded. The proof is the one #2207 heals the type lane on, so a type
/// with a stream genuinely missing (its roll-up then disagrees), an undecided type, or a window that does not cover the
/// whole history removes no per-stream row.
/// </summary>
public partial class IntegrityManifestReceptorTests {
  private static async Task<IntegrityRepairLedger.DivergenceKey[]> _seedStreamRowsAsync(
      IntegrityRepairLedger ledger, Guid origin, int count, string eventType = LANE_TYPE) {
    var keys = Enumerable.Range(0, count)
      .Select(_ => new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", eventType, TrackedGuid.New().Value)).ToArray();
    foreach (var key in keys) {
      _ = await ledger.TryBeginReportAsync(key, 41, 42, 0, 0, _fixedNow, TimeSpan.FromMinutes(60));
    }
    return keys;
  }

  [Test]
  public async Task TypeLevel_AFullHistoryMatch_ForgetsEveryStreamRowOfTheType_AndRecordsEachHealAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    await _seedTypeLaneAsync(ledger, coordinator.OriginId);
    _ = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 3);
    coordinator.ReceivedTypeDigests = [_typeDigest(LANE_TYPE, 41, 42, 5)];

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(ledger.Count).IsEqualTo(0)
      .Because("the whole type compares equal, so no stream of it can be missing anything: every per-stream row is a phantom");
    await Assert.That(heals()).IsEqualTo(4L).Because("the type lane and the three stream rows are each a heal");
  }

  [Test]
  public async Task TypeLevel_AWindowAtTheSeal_ThatReachedTheSettledHead_ForgetsTheTypesStreamRowsAsync() {
    var (coordinator, ledger, receptor, _) = _typeLaneFixture();
    _ = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2);
    coordinator.Seal = 100;
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types)));

    await Assert.That(ledger.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TypeLevel_AStreamGenuinelyMissing_KeepsEveryStreamRowOfTheTypeAsync() {
    var (coordinator, ledger, receptor, heals) = _typeLaneFixture();
    var rows = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2);
    // The origin holds five events of the type and this service four: one stream is genuinely short, so the roll-up
    // disagrees and the type is not proven.
    coordinator.ReceivedTypeDigests = [_typeDigest(LANE_TYPE, 40, 42, 4)];

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(ledger.Count).IsEqualTo(rows.Length)
      .Because("a genuinely missing stream makes the type's roll-up disagree, so no stream row of the type may be removed");
    await Assert.That(heals()).IsEqualTo(0L);
  }

  [Test]
  public async Task TypeLevel_AMatchingWindowAboveTheSeal_KeepsTheTypesStreamRowsAsync() {
    var (coordinator, ledger, receptor, _) = _typeLaneFixture();
    var rows = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2);
    coordinator.Seal = 50;
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types)));

    await Assert.That(ledger.Count).IsEqualTo(rows.Length)
      .Because("a window above the seal proves only itself; a stream row's deficit may lie in the history before it");
  }

  [Test]
  public async Task TypeLevel_AMatchingWindowTheOriginCapped_KeepsTheTypesStreamRowsAsync() {
    var (coordinator, ledger, receptor, _) = _typeLaneFixture();
    var rows = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2);
    coordinator.Seal = 100;
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 41, 42, 5)], ComputedThrough = 300 };

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 41, 42, 5)], ManifestLevel.Types))
      with { WindowCapped = true });

    await Assert.That(ledger.Count).IsEqualTo(rows.Length);
  }

  [Test]
  public async Task TypeLevel_AnUndecidedType_KeepsTheTypesStreamRowsAsync() {
    var (coordinator, ledger, receptor, _) = _typeLaneFixture();
    var rows = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2);
    coordinator.Seal = 100;
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest(LANE_TYPE, 0x1, 0x1, 3)], ComputedThrough = 300 };
    coordinator.UnsequencedDigests = [_typeDigest(LANE_TYPE, 0x40, 0x80, 2)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest(LANE_TYPE, 0x7, 0x7, 5)], ManifestLevel.Types)));

    await Assert.That(ledger.Count).IsEqualTo(rows.Length);
  }

  [Test]
  public async Task TypeLevel_OneTypeMatchesAndAnotherDiverges_ForgetsOnlyTheMatchingTypesStreamRowsAsync() {
    var (coordinator, ledger, receptor, _) = _typeLaneFixture();
    _ = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2);
    var diverging = await _seedStreamRowsAsync(ledger, coordinator.OriginId, 2, "Contracts.TypeY");
    coordinator.ReceivedTypeDigests = [_typeDigest(LANE_TYPE, 41, 42, 5), _typeDigest("Contracts.TypeY", 1, 2, 3)];

    await receptor.HandleAsync(_manifest(coordinator,
      [_typeDigest(LANE_TYPE, 41, 42, 5), _typeDigest("Contracts.TypeY", 7, 8, 9)], ManifestLevel.Types));

    await Assert.That(ledger.Count).IsEqualTo(diverging.Length)
      .Because("only the proven type's rows are phantoms; the diverging type's rows are real until it compares equal");
  }
}
