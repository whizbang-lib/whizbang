// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// The claimer's cached assignment (#1254): read once, refreshed only when told, presented until its lease runs out,
/// and never in the way of a claim. Pure: a fake clock and an in-memory store, no threads.
/// </summary>
[Category("Workers")]
public class PartitionAssignmentCacheTests {
  private static readonly Guid _self = new("00000000-0000-0000-0000-000000000005");
  private static readonly Guid _peer = new("00000000-0000-0000-0000-00000000000a");

  private static PartitionAssignmentRead _read(long epoch, long revision, TimeSpan remaining, params Guid[] members) =>
    new(new PartitionAssignment(epoch, revision, _peer, members, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1)), remaining);

  private static (PartitionAssignmentCache Cache, FakePartitionAssignmentStore Store, FakeTimeProvider Clock) _create() {
    var store = new FakePartitionAssignmentStore();
    var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
    return (new PartitionAssignmentCache(store, NullLogger<PartitionAssignmentCache>.Instance, clock), store, clock);
  }

  [Test]
  public async Task ForClaim_TheFirstTime_ReadsOnceAndPresentsTheVersionAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 2, TimeSpan.FromSeconds(90), _self, _peer);

    var first = await cache.ForClaimAsync(_self, CancellationToken.None);
    var second = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(first).IsEqualTo(new PartitionAssignmentVersion(3, 2));
    await Assert.That(second).IsEqualTo(first);
    await Assert.That(store.Reads).IsEqualTo(1).Because("a current copy answers from memory: no added work per claim");
  }

  [Test]
  public async Task ForClaim_WhenNothingIsPublished_PresentsNothingAsync() {
    var (cache, store, _) = _create();

    await Assert.That(await cache.ForClaimAsync(_self, CancellationToken.None)).IsNull();
    await Assert.That(cache.Current).IsNull();
    await Assert.That(store.Reads).IsEqualTo(1);
  }

  [Test]
  public async Task ForClaim_WhenNotAMember_PresentsNothingAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _peer);

    await Assert.That(await cache.ForClaimAsync(_self, CancellationToken.None)).IsNull()
      .Because("an instance the assigner left out ranks itself until the next publish includes it");
  }

  [Test]
  public async Task ForClaim_AfterTheLeaseRunsOut_PresentsNothingAsync() {
    var (cache, store, clock) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    clock.Advance(TimeSpan.FromSeconds(89));
    await Assert.That(await cache.ForClaimAsync(_self, CancellationToken.None)).IsNotNull()
      .Because("the last assignment is kept until it expires");
    clock.Advance(TimeSpan.FromSeconds(1));
    await Assert.That(await cache.ForClaimAsync(_self, CancellationToken.None)).IsNull()
      .Because("past its lease the claim falls back to ranking itself");
  }

  [Test]
  public async Task MarkStale_TheNextClaimRefreshes_AndAChangeIsAnnouncedAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);
    var changes = new List<PartitionAssignment?>();
    cache.OnAssignmentChanged += changes.Add;

    store.Published = _read(3, 2, TimeSpan.FromSeconds(90), _self, _peer);
    cache.MarkStale();
    var version = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(version).IsEqualTo(new PartitionAssignmentVersion(3, 2));
    await Assert.That(changes).Count().IsEqualTo(1);
    await Assert.That(changes[0]!.Members).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Refresh_ToTheSameVersion_AnnouncesNoChangeButReportsTheRefreshAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);
    var changes = 0;
    var refreshes = new List<PartitionAssignment?>();
    cache.OnAssignmentChanged += _ => changes++;
    cache.OnRefreshed += refreshes.Add;

    cache.MarkStale();
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(changes).IsEqualTo(0);
    await Assert.That(refreshes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Refresh_ToNothingPublished_ClearsTheCopyAndAnnouncesItAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);
    var changes = new List<PartitionAssignment?>();
    cache.OnAssignmentChanged += changes.Add;

    store.Published = null;
    cache.MarkStale();
    var version = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(version).IsNull();
    await Assert.That(changes).IsEquivalentTo(new PartitionAssignment?[] { null });
  }

  [Test]
  public async Task Refresh_ThatFails_KeepsTheLastCopy_AndTriesAgainNextClaimAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    store.FailReadsWith = new InvalidOperationException("database unavailable");
    cache.MarkStale();
    var during = await cache.ForClaimAsync(_self, CancellationToken.None);
    store.FailReadsWith = null;
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(during).IsEqualTo(new PartitionAssignmentVersion(3, 1)).Because("the last assignment is kept until it expires");
    await Assert.That(store.Reads).IsEqualTo(3).Because("the failed read is retried on the next claim");
  }

  [Test]
  public async Task Refresh_ThatIsCanceled_IsNotSwallowedAsync() {
    var (cache, store, _) = _create();
    store.FailReadsWith = new OperationCanceledException();

    await Assert.That(async () => await cache.ForClaimAsync(_self, CancellationToken.None)).Throws<OperationCanceledException>()
      .Because("a canceled claim is not a failed read to keep a stale copy for");
  }

  [Test]
  public async Task OnNotification_ForADifferentVersion_MarksTheCopyStaleAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    cache.OnNotification("3:2");
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(store.Reads).IsEqualTo(2);
    await Assert.That(cache.ChannelName).IsEqualTo(PartitionAssignmentCache.CHANNEL);
  }

  [Test]
  public async Task OnNotification_ForTheVersionAlreadyHeld_ReadsNothingAsync() {
    var (cache, store, _) = _create();
    store.Published = _read(3, 1, TimeSpan.FromSeconds(90), _self);
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    cache.OnNotification("3:1");
    _ = await cache.ForClaimAsync(_self, CancellationToken.None);

    await Assert.That(store.Reads).IsEqualTo(1);
  }

  [Test]
  public async Task OnNotification_RaisesTheAnnouncementSignalAsync() {
    var (cache, _, _) = _create();
    var announced = new List<string>();
    cache.OnPublishAnnounced += announced.Add;

    cache.OnNotification("7:1");

    await Assert.That(announced).IsEquivalentTo(["7:1"]);
  }

  [Test]
  public async Task Constructor_RequiresAStoreAndALoggerAsync() {
    await Assert.That(() => new PartitionAssignmentCache(null!, NullLogger<PartitionAssignmentCache>.Instance)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignmentCache(new FakePartitionAssignmentStore(), null!)).Throws<ArgumentNullException>();
    await Assert.That(new PartitionAssignmentCache(new FakePartitionAssignmentStore(), NullLogger<PartitionAssignmentCache>.Instance).Current).IsNull();
  }
}
