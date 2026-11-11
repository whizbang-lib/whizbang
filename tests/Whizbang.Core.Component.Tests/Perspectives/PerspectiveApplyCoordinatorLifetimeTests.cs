// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// <para>Locks the lifetime of the per-key apply locks held by <see cref="PerspectiveApplyCoordinator"/>
/// (#2227). The coordinator used to keep one semaphore for every (stream, perspective) pair the process
/// had ever applied, so memory grew with the number of distinct streams for the life of the process. A
/// key's lock now exists only while a holder or a waiter references it.</para>
/// <para>Component tier: a contended acquire completes on the thread pool when the holder releases, so
/// the waiter's completion is a second flow of control.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveApplyCoordinator.cs</code-under-test>
[Category("Shard2")]
public sealed class PerspectiveApplyCoordinatorLifetimeTests {
  private const string PERSPECTIVE = "Orders.Projection";

  private static PerspectiveApplyCoordinator _newCoordinator() =>
    new(NullLogger<PerspectiveApplyCoordinator>.Instance);

  [Test]
  public async Task DistinctStreams_AfterEveryApplyReleases_CoordinatorHoldsNoEntriesAsync() {
    var coordinator = _newCoordinator();
    var handles = new List<IAsyncDisposable>();
    for (var i = 0; i < 500; i++) {
      handles.Add(await coordinator.AcquireAsync(Guid.NewGuid(), PERSPECTIVE));
    }

    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(500)
      .Because("each held key owns exactly one lock while it is held");

    foreach (var handle in handles) {
      await handle.DisposeAsync();
    }

    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(0)
      .Because("a lock nobody holds or waits on must be released, or memory grows with every stream "
             + "the process has ever applied (#2227)");
  }

  [Test]
  public async Task QueuedWaiter_KeepsTheKeysLock_AndStillSerializesWithTheNextAcquireAsync() {
    var coordinator = _newCoordinator();
    var streamId = Guid.NewGuid();

    var holder = await coordinator.AcquireAsync(streamId, PERSPECTIVE);
    var waiterTask = coordinator.AcquireAsync(streamId, PERSPECTIVE);
    await Assert.That(waiterTask.IsCompleted).IsFalse();

    await holder.DisposeAsync();
    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(1)
      .Because("the holder's release must not drop a lock a waiter is queued on: a later acquire "
             + "would be handed a fresh lock and run alongside the waiter");

    var waiter = await waiterTask;
    var third = coordinator.AcquireAsync(streamId, PERSPECTIVE);
    await Assert.That(third.IsCompleted).IsFalse()
      .Because("the waiter now holds the key's lock, so a third acquire must wait for it");

    await waiter.DisposeAsync();
    var thirdHandle = await third;
    await thirdHandle.DisposeAsync();

    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(0);
  }

  [Test]
  public async Task CanceledWaiter_ReleasesItsReference_SoTheLockIsDroppedWithTheHolderAsync() {
    var coordinator = _newCoordinator();
    var streamId = Guid.NewGuid();

    var holder = await coordinator.AcquireAsync(streamId, PERSPECTIVE);
    using var cts = new CancellationTokenSource();
    var waiterTask = coordinator.AcquireAsync(streamId, PERSPECTIVE, cts.Token);
    await cts.CancelAsync();

    await Assert.That(async () => await waiterTask).Throws<OperationCanceledException>();
    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(1)
      .Because("only the holder still references the key once the canceled waiter is gone");

    await holder.DisposeAsync();
    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(0)
      .Because("a canceled wait must give back its reference, or its key's lock is never released");
  }

  [Test]
  public async Task CanceledBeforeAcquire_DoesNotLeaveAnEntryAsync() {
    var coordinator = _newCoordinator();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.That(async () => await coordinator.AcquireAsync(Guid.NewGuid(), PERSPECTIVE, cts.Token))
      .Throws<OperationCanceledException>();
    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(0);
  }

  [Test]
  public async Task HandleDisposedTwice_ReleasesItsReferenceOnceAsync() {
    var coordinator = _newCoordinator();
    var streamId = Guid.NewGuid();

    var holder = await coordinator.AcquireAsync(streamId, PERSPECTIVE);
    var waiterTask = coordinator.AcquireAsync(streamId, PERSPECTIVE);

    await holder.DisposeAsync();
    await holder.DisposeAsync();

    var waiter = await waiterTask;
    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(1)
      .Because("a second dispose of the same handle must not give back the waiter's reference");
    var next = coordinator.AcquireAsync(streamId, PERSPECTIVE);
    await Assert.That(next.IsCompleted).IsFalse();

    await waiter.DisposeAsync();
    await (await next).DisposeAsync();
    await Assert.That(coordinator.TrackedKeyCount).IsEqualTo(0);
  }
}
