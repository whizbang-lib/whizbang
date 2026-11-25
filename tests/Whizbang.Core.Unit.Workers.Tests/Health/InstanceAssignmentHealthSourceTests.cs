// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Startup;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// #2595: the <c>assignment</c> health component says what this instance was given. Awaiting its share is Operational
/// inside the grace and Degraded past it; readiness never waits for a share. Never Faulted.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Health/InstanceAssignmentHealthSource.cs</code-under-test>
[Category("Health")]
public class InstanceAssignmentHealthSourceTests {
  private static async Task<(ComponentHealth Health, FakeTimeProvider Time, InstanceAssignmentMonitor Monitor, QueuedAssignmentReader Reader, FixedInstance Instance)>
      _setupAsync(Action<QueuedAssignmentReader, FixedInstance>? answer) {
    var instance = new FixedInstance();
    var reader = new QueuedAssignmentReader();
    var time = new FakeTimeProvider();
    var monitor = InstanceAssignments.Monitor(reader, instance, time);
    if (answer is not null) {
      answer(reader, instance);
      _ = await monitor.ObserveAsync(CancellationToken.None);
    }
    var health = await new InstanceAssignmentHealthSource(monitor, time).ReportAsync(CancellationToken.None);
    return (health, time, monitor, reader, instance);
  }

  [Test]
  public async Task AFreshInstance_NotYetObserved_IsOperational_LikeOneAwaitingItsShareInsideTheGraceAsync() {
    // A fresh instance is where one awaiting its share is: on time, until the grace runs out.
    var (unstarted, _, _, _, _) = await _setupAsync(null);
    await Assert.That(unstarted.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(unstarted.Detail).IsEqualTo("not yet observed");
    var (unstartedFailed, _, _, _, _) = await _setupAsync((r, _) => r.Fail(new InvalidOperationException("down")));
    await Assert.That(unstartedFailed.State).IsEqualTo(ComponentState.Operational)
      .Because("before the loop starts there is no grace to run out, only the failure to name");
    await Assert.That(unstartedFailed.Detail).IsEqualTo("not yet observed; last read: the assignment could not be read: InvalidOperationException");

    var time = new FakeTimeProvider();
    var monitor = InstanceAssignments.Monitor(new QueuedAssignmentReader(), new FixedInstance(), time);
    monitor.MarkStarted();
    var source = new InstanceAssignmentHealthSource(monitor, time);
    time.Advance(monitor.AwaitingShareGrace);
    var atTheGrace = await source.ReportAsync(CancellationToken.None);
    await Assert.That(atTheGrace.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(atTheGrace.Detail).IsEqualTo("not yet observed, 15s of a 15s grace since the monitor started");
  }

  [Test]
  public async Task NoSuccessfulReadPastTheGrace_IsDegraded_NamingTheLastFailureAsync() {
    var time = new FakeTimeProvider();
    var reader = new QueuedAssignmentReader();
    var monitor = InstanceAssignments.Monitor(reader, new FixedInstance(), time);
    var source = new InstanceAssignmentHealthSource(monitor, time);
    monitor.MarkStarted();
    reader.Fail(new InvalidOperationException("down"));
    _ = await monitor.ObserveAsync(CancellationToken.None);

    var inside = await source.ReportAsync(CancellationToken.None);
    await Assert.That(inside.State).IsEqualTo(ComponentState.Operational)
      .Because("a read that fails before the first success is still inside the start's grace");
    await Assert.That(inside.Detail).IsEqualTo(
      "not yet observed, 0s of a 15s grace since the monitor started; last read: the assignment could not be read: InvalidOperationException");

    time.Advance(monitor.AwaitingShareGrace + TimeSpan.FromSeconds(1));
    var past = await source.ReportAsync(CancellationToken.None);
    await Assert.That(past.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(past.Detail).IsEqualTo("not yet observed, 16s of a 15s grace since the monitor started: past the grace; "
      + "last read: the assignment could not be read: InvalidOperationException");
  }

  [Test]
  public async Task AStandardWorker_IsOperational_SayingWhatItWasGivenAsync() {
    var (health, _, _, _, _) = await _setupAsync((r, i) => r.Answer(InstanceAssignments.Row(i.InstanceId, InstanceAssignmentKind.StandardWorker)));
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(health.Detail).IsEqualTo("Assigned: standard worker, 2500 partitions, map 12");
  }

  [Test]
  public async Task AwaitingItsShare_IsOperationalInsideTheGrace_AndDegradedPastIt_NeverFaultedAsync() {
    var (inside, time, monitor, _, _) = await _setupAsync((r, i) => r.Answer(InstanceAssignments.Row(i.InstanceId, InstanceAssignmentKind.AwaitingShare, owned: 0)));
    await Assert.That(inside.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(inside.Detail).IsEqualTo("Assigned: awaiting its share, map 12, for 0s of a 15s grace");

    var source = new InstanceAssignmentHealthSource(monitor, time);
    time.Advance(monitor.AwaitingShareGrace);
    await Assert.That((await source.ReportAsync(CancellationToken.None)).State).IsEqualTo(ComponentState.Operational)
      .Because("at the grace exactly the share is still on time");
    time.Advance(TimeSpan.FromSeconds(1));
    var past = await source.ReportAsync(CancellationToken.None);
    await Assert.That(past.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(past.Detail)
      .IsEqualTo("Assigned: awaiting its share, map 12, for 16s of a 15s grace: past the grace, no published map gives it a partition");
  }

  [Test]
  public async Task AFailedRead_AfterASuccessfulOne_ANotRegisteredInstance_AndAnEvictedOne_AreDegradedAsync() {
    var (_, time, monitor, reader, instance) = await _setupAsync((r, i) =>
      r.Answer(InstanceAssignments.Row(i.InstanceId, InstanceAssignmentKind.StandardWorker)));
    reader.Fail(new InvalidOperationException("down"));
    _ = await monitor.ObserveAsync(CancellationToken.None);
    var failed = await new InstanceAssignmentHealthSource(monitor, time).ReportAsync(CancellationToken.None);
    await Assert.That(failed.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(failed.Detail).IsEqualTo("the assignment could not be read: InvalidOperationException");
    await Assert.That(instance.InstanceId).IsNotEqualTo(Guid.Empty);

    var (missing, _, _, _, _) = await _setupAsync((r, _) => r.Answer());
    await Assert.That(missing.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(missing.Detail).IsEqualTo("this instance is not in the registered fleet");

    var (evicted, _, _, _, _) = await _setupAsync((r, i) => r.Answer(InstanceAssignments.Row(i.InstanceId, InstanceAssignmentKind.Evicted)));
    await Assert.That(evicted.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(evicted.Detail).IsEqualTo("Assigned: evicted");
  }

  [Test]
  public async Task TheComponent_IsNamedAssignment_AndRefusesANullMonitorAsync() {
    var monitor = InstanceAssignments.Monitor(null, new FixedInstance(), new FakeTimeProvider());
    await Assert.That(new InstanceAssignmentHealthSource(monitor).Component).IsEqualTo("assignment");
    await Assert.That(() => new InstanceAssignmentHealthSource(null!)).Throws<ArgumentNullException>();
  }
}
