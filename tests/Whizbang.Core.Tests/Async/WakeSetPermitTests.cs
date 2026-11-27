// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Async;

namespace Whizbang.Core.Tests.Async;

/// <summary>
/// A semaphore as a <see cref="WakeSet"/> source: a permit acquired for it is the caller's only once taken, and every
/// permit acquired and not taken is handed back exactly once, whether it was acquired before the set was disposed or
/// lands after. A permit is never kept by a source that lost, and never released twice.
/// </summary>
/// <remarks>
/// The semaphores have a maximum count, so a second release of one permit throws instead of passing silently. A wait for
/// a permit to come back is a wait on the semaphore itself, bounded only so a broken hand-back fails the test.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Async/WakeSet.cs</code-under-test>
[Category("Unit")]
public class WakeSetPermitTests {
  private static readonly TimeSpan _bound = TimeSpan.FromSeconds(30);

  [Test]
  public async Task APermitThatFrees_WakesTheSet_AndIsTakenExactlyOnceAsync() {
    using var semaphore = new SemaphoreSlim(0, 1);
    var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var permit = set.Add(semaphore);

    var waiting = set.WaitAsync(Timeout.InfiniteTimeSpan);
    var parked = !waiting.IsCompleted;
    var notYet = permit.TryTake();
    semaphore.Release();
    var wake = await waiting.AsTask().WaitAsync(_bound);
    var taken = permit.TryTake();
    var takenAgain = permit.TryTake();
    set.Dispose();

    await Assert.That(parked).IsTrue();
    await Assert.That(notYet).IsFalse();
    await Assert.That(wake.Woke(permit)).IsTrue();
    await Assert.That(taken).IsTrue();
    await Assert.That(takenAgain).IsFalse()
      .Because("one acquired permit is taken once");
    await Assert.That(semaphore.CurrentCount).IsEqualTo(0)
      .Because("a taken permit is the caller's: disposing the set does not hand it back");
    semaphore.Release();
  }

  [Test]
  public async Task AFreePermit_IsReadyOnTheFirstPass_AndAfterATake_TheNextPassAcquiresAnotherAsync() {
    using var semaphore = new SemaphoreSlim(2, 2);
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var permit = set.Add(semaphore);

    var first = await set.WaitAsync(TimeSpan.Zero);
    var firstTaken = permit.TryTake();
    var second = await set.WaitAsync(TimeSpan.Zero);
    var secondTaken = permit.TryTake();
    var third = await set.WaitAsync(TimeSpan.Zero);

    await Assert.That(first.Woke(permit) && firstTaken).IsTrue();
    await Assert.That(second.Woke(permit) && secondTaken).IsTrue();
    await Assert.That(third.IsTimeout).IsTrue()
      .Because("both permits are taken, so the third acquire is still pending");
    await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    semaphore.Release(2);
  }

  [Test]
  public async Task AHeldPermitNotTaken_StaysHeldAndReady_UntilTakenAsync() {
    using var semaphore = new SemaphoreSlim(1, 1);
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var permit = set.Add(semaphore);

    var first = await set.WaitAsync(TimeSpan.Zero);
    var second = await set.WaitAsync(TimeSpan.Zero);

    await Assert.That(first.Woke(permit) && second.Woke(permit)).IsTrue()
      .Because("a held permit is a level: it wakes every pass until the caller takes it");
    await Assert.That(semaphore.CurrentCount).IsEqualTo(0)
      .Because("the pass after did not acquire a second permit for a source that already holds one");
  }

  /// <summary>Both permits free at once: the caller takes one, and the other source's permit goes back.</summary>
  [Test]
  public async Task TwoSourcesBothAcquired_TheOneNotTaken_IsHandedBackOnDisposeAsync() {
    using var shared = new SemaphoreSlim(1, 1);
    using var reserve = new SemaphoreSlim(1, 1);
    var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var sharedPermit = set.Add(shared);
    var reservePermit = set.Add(reserve);

    var wake = await set.WaitAsync(TimeSpan.Zero);
    var tookShared = sharedPermit.TryTake();
    set.Dispose();

    await Assert.That(wake.Woke(sharedPermit) && wake.Woke(reservePermit)).IsTrue();
    await Assert.That(tookShared).IsTrue();
    await Assert.That(reserve.CurrentCount).IsEqualTo(1)
      .Because("the losing source's permit is handed back, so the reserve is whole for the next caller");
    await Assert.That(shared.CurrentCount).IsEqualTo(0);
    await Assert.That(reservePermit.TryTake()).IsFalse()
      .Because("a permit handed back is no longer the source's to take");
    shared.Release();
  }

  /// <summary>
  /// The contended hand-back: the caller won on one semaphore while its acquire on the other was queued; that queued
  /// acquire lands when the other semaphore frees, after the set is gone, and its permit goes straight back.
  /// </summary>
  [Test]
  public async Task AQueuedAcquireThatLandsAfterTheSetIsDisposed_IsHandedBackAsync() {
    using var shared = new SemaphoreSlim(0, 1);
    using var reserve = new SemaphoreSlim(0, 1);
    var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var sharedPermit = set.Add(shared);
    set.Add(reserve);

    var waiting = set.WaitAsync(Timeout.InfiniteTimeSpan);
    shared.Release();
    await waiting.AsTask().WaitAsync(_bound);
    var took = sharedPermit.TryTake();
    // The reserve frees while the set still holds a queued acquire on it, and the set is disposed straight after:
    // either the acquire is canceled first and holds nothing, or it lands and is handed back. The permit returns.
    reserve.Release();
    set.Dispose();
    var reserveCameBack = await reserve.WaitAsync(_bound);

    await Assert.That(took).IsTrue();
    await Assert.That(reserveCameBack).IsTrue()
      .Because("the reserve's permit is not kept by a source that lost the race");
    reserve.Release();
    shared.Release();
  }

  [Test]
  public async Task AnAcquireStillQueuedWhenTheSetIsDisposed_IsCanceled_AndHoldsNoPermitAsync() {
    using var semaphore = new SemaphoreSlim(0, 1);
    var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    set.Add(semaphore);

    var timedOut = await set.WaitAsync(TimeSpan.Zero);
    set.Dispose();
    semaphore.Release();
    var freedForAnotherCaller = await semaphore.WaitAsync(_bound);

    await Assert.That(timedOut.IsTimeout).IsTrue();
    await Assert.That(freedForAnotherCaller).IsTrue()
      .Because("a canceled acquire leaves no waiter that would take the next permit");
  }

  /// <summary>Two sets on one semaphore: a permit handed back by one is acquired by the other, and counted once.</summary>
  [Test]
  public async Task APermitHandedBackByOneSet_IsAcquiredByTheNext_AndNeverDoubleCountedAsync() {
    using var semaphore = new SemaphoreSlim(1, 1);
    var first = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    using var second = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    first.Add(semaphore);
    var secondPermit = second.Add(semaphore);

    var firstWake = await first.WaitAsync(TimeSpan.Zero);
    var secondWaiting = second.WaitAsync(Timeout.InfiniteTimeSpan);
    var secondParked = !secondWaiting.IsCompleted;
    first.Dispose();
    var secondWake = await secondWaiting.AsTask().WaitAsync(_bound);
    var secondTook = secondPermit.TryTake();

    await Assert.That(firstWake.IsTimeout).IsFalse();
    await Assert.That(secondParked).IsTrue();
    await Assert.That(secondWake.Woke(secondPermit) && secondTook).IsTrue()
      .Because("the permit the first set handed back is the one the second set's queued acquire receives");
    await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    semaphore.Release();
    await Assert.That(semaphore.CurrentCount).IsEqualTo(1)
      .Because("the permit exists once: a second hand-back would have thrown on the semaphore's maximum");
  }
}
