// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A purge-marker store that does not take part in the backfill (#2314) inherits defaults that write and count nothing,
/// so a store without the backfill never marks a pair it cannot prove was purged.
/// </summary>
public class PerspectivePurgeMarkerStoreDefaultsTests {
  private static readonly IReadOnlyList<(string PerspectiveName, string EventType)> _purgingTypes =
    [("Orders", "Shop.OrderDeleted, Shop")];

  [Test]
  public async Task BackfillMarkers_OnAStoreWithoutTheBackfill_WritesNoneAsync() {
    IPerspectivePurgeMarkerStore store = new StoreWithoutBackfill();

    var written = await store.BackfillMarkersAsync(_purgingTypes, batchSize: 500);

    await Assert.That(written).IsEqualTo(0);
  }

  [Test]
  public async Task CountUnmarkedRowlessPairs_OnAStoreWithoutTheBackfill_CountsNoneAsync() {
    IPerspectivePurgeMarkerStore store = new StoreWithoutBackfill();

    var counted = await store.CountUnmarkedRowlessPairsAsync(_purgingTypes, cap: 10_000);

    await Assert.That(counted).IsEqualTo(0L);
  }

  private sealed class StoreWithoutBackfill : IPerspectivePurgeMarkerStore {
    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(false);

    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }
}
