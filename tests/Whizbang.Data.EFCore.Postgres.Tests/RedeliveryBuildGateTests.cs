// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The one-build-at-a-time gate of redelivery (stream-integrity R1b) says what it is doing: whether a build holds it, and
/// how many requests wait for that build, with a signal for the moment a given number are waiting. A request that passes
/// a free gate never counts as queued.
/// </summary>
/// <docs>resilience/stream-integrity#one-repair-build-at-a-time</docs>
[Category("Shard1")]
public class RedeliveryBuildGateTests {
  [Test]
  public async Task ARequestAtAFreeGate_Builds_WithoutQueueing_AndTheNextWaitsUntilItEndsAsync() {
    var gate = new RedeliveryBuildGate();
    var oneQueued = gate.WhenQueuedAsync(1);

    await gate.EnterAsync(CancellationToken.None);
    var building = gate.Building;
    var queuedWhileFree = oneQueued.IsCompleted;
    var second = gate.EnterAsync(CancellationToken.None);
    await oneQueued;
    var queuedBehind = gate.Queued;
    var secondInBeforeExit = second.IsCompleted;
    gate.Exit();
    await second;

    await Assert.That(building).IsTrue();
    await Assert.That(queuedWhileFree).IsFalse().Because("a request that passes a free gate never counts as queued");
    await Assert.That(queuedBehind).IsEqualTo(1);
    await Assert.That(secondInBeforeExit).IsFalse().Because("one build at a time");
    await Assert.That(gate.Queued).IsEqualTo(0).Because("the queued request is building now");
    await Assert.That(gate.Building).IsTrue();
    gate.Exit();
    await Assert.That(gate.Building).IsFalse();
  }

  [Test]
  public async Task WhenQueued_CompletesAtOnce_WhenThatManyAlreadyWait_AndNotForFewerAsync() {
    var gate = new RedeliveryBuildGate();
    await gate.EnterAsync(CancellationToken.None);
    var twoQueued = gate.WhenQueuedAsync(2);
    var first = gate.EnterAsync(CancellationToken.None);
    await gate.WhenQueuedAsync(1);
    var twoAfterOne = twoQueued.IsCompleted;
    var second = gate.EnterAsync(CancellationToken.None);
    await twoQueued;

    await Assert.That(twoAfterOne).IsFalse().Because("one waiting request is not two");
    await Assert.That(gate.WhenQueuedAsync(2).IsCompleted).IsTrue().Because("two already wait");
    await Assert.That(() => gate.WhenQueuedAsync(0)).Throws<ArgumentOutOfRangeException>();
    gate.Exit();
    await first;
    gate.Exit();
    await second;
    gate.Exit();
  }

  [Test]
  public async Task ACanceledWait_LeavesTheQueue_AndTheGateStaysWithItsBuildAsync() {
    var gate = new RedeliveryBuildGate();
    await gate.EnterAsync(CancellationToken.None);
    using var cancel = new CancellationTokenSource();
    var waiting = gate.EnterAsync(cancel.Token);
    await gate.WhenQueuedAsync(1);

    await cancel.CancelAsync();

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(gate.Queued).IsEqualTo(0);
    await Assert.That(gate.Building).IsTrue().Because("the build in progress still holds the gate");
    gate.Exit();
  }

  [Test]
  public async Task Process_IsOneGate_SharedByEveryReceptorWhoseContainerRegistersNoneAsync() {
    await Assert.That(RedeliveryBuildGate.Process).IsSameReferenceAs(RedeliveryBuildGate.Process);
  }
}
