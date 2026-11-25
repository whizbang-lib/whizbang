// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2595: each instance says what it was given. The monitor logs "Assigned: ..." when its assignment first appears and
/// on every change, never on a read that changed nothing, and keeps since when it has awaited its share, across a
/// failed read.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/InstanceAssignmentMonitor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/InstanceAssignmentOptions.cs</code-under-test>
[Category("Startup")]
public class InstanceAssignmentMonitorTests {
  private static List<string> _assigned(FakeLogger<InstanceAssignmentMonitor> logger) =>
    [.. logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Information).Select(r => r.Message)];

  [Test]
  public async Task Observe_LogsTheAssignment_WhenItFirstAppears_AndOnlyOnEveryChangeAsync() {
    var instance = new FixedInstance();
    var reader = new QueuedAssignmentReader();
    var logger = new FakeLogger<InstanceAssignmentMonitor>();
    var monitor = InstanceAssignments.Monitor(reader, instance, new FakeTimeProvider(), logger);
    var other = InstanceAssignments.Row(Guid.CreateVersion7(), InstanceAssignmentKind.StandardWorker);
    reader.Answer(other, InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.StandardWorker));
    reader.Answer(other, InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.StandardWorker));
    reader.Answer(InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.RoleHolder, 2500, 13,
      Whizbang.Core.Notifications.CommitOrderStamperOptions.ROLE, PartitionAssignerOptions.ROLE));
    reader.Answer(InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.RoleHolder, 3000, 13,
      Whizbang.Core.Notifications.CommitOrderStamperOptions.ROLE, PartitionAssignerOptions.ROLE));
    reader.Answer(InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.RoleHolder, 3000, 13,
      PartitionAssignerOptions.ROLE));

    for (var i = 0; i < 5; i++) {
      _ = await monitor.ObserveAsync(CancellationToken.None);
    }

    await Assert.That(_assigned(logger)).IsEquivalentTo([
      "Assigned: standard worker, 2500 partitions, map 12",
      "Assigned: role holder (commit-stamper, partition-assigner), 2500 partitions, map 13",
      "Assigned: role holder (commit-stamper, partition-assigner), 3000 partitions, map 13",
      "Assigned: role holder (partition-assigner), 3000 partitions, map 13",
    ]).Because("a read that changed nothing logs nothing; a new map, a new share or a new role each log once");
    await Assert.That(monitor.Latest!.Assignment!.Kind).IsEqualTo(InstanceAssignmentKind.RoleHolder);
  }

  [Test]
  public async Task Describe_NamesEveryKindAsync() {
    var id = Guid.CreateVersion7();
    await Assert.That(InstanceAssignmentMonitor.Describe(InstanceAssignments.Row(id, InstanceAssignmentKind.AwaitingShare, owned: 0, map: null)))
      .IsEqualTo("awaiting its share, no map published");
    await Assert.That(InstanceAssignmentMonitor.Describe(InstanceAssignments.Row(id, InstanceAssignmentKind.Starting)))
      .IsEqualTo("starting, not yet eligible for work");
    await Assert.That(InstanceAssignmentMonitor.Describe(InstanceAssignments.Row(id, InstanceAssignmentKind.Draining))).IsEqualTo("draining");
    await Assert.That(InstanceAssignmentMonitor.Describe(InstanceAssignments.Row(id, InstanceAssignmentKind.Ineligible)))
      .IsEqualTo("ineligible: an older release in a service that claims by the partition map");
    await Assert.That(InstanceAssignmentMonitor.Describe(InstanceAssignments.Row(id, InstanceAssignmentKind.Evicted))).IsEqualTo("evicted");
    await Assert.That(() => InstanceAssignmentMonitor.Describe(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Observe_AwaitingItsShare_KeepsSinceWhen_AcrossAFailedRead_UntilItHasOneAsync() {
    var instance = new FixedInstance();
    var reader = new QueuedAssignmentReader();
    var time = new FakeTimeProvider();
    var logger = new FakeLogger<InstanceAssignmentMonitor>();
    var monitor = InstanceAssignments.Monitor(reader, instance, time, logger);
    var started = time.GetUtcNow();
    var awaiting = InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.AwaitingShare, owned: 0);
    reader.Answer(awaiting);
    reader.Fail(new InvalidOperationException("down"));
    reader.Answer(awaiting);
    reader.Answer(InstanceAssignments.Row(instance.InstanceId, InstanceAssignmentKind.StandardWorker));

    await Assert.That((await monitor.ObserveAsync(CancellationToken.None)).AwaitingSince).IsEqualTo(started);
    time.Advance(TimeSpan.FromSeconds(10));
    var failed = await monitor.ObserveAsync(CancellationToken.None);
    await Assert.That(failed.Failure).IsEqualTo("the assignment could not be read: InvalidOperationException");
    await Assert.That(failed.AwaitingSince).IsEqualTo(started).Because("one bad read never restarts the grace");
    time.Advance(TimeSpan.FromSeconds(10));
    await Assert.That((await monitor.ObserveAsync(CancellationToken.None)).AwaitingSince).IsEqualTo(started);
    await Assert.That((await monitor.ObserveAsync(CancellationToken.None)).AwaitingSince).IsNull();
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning)).IsEqualTo(1)
      .Because("a failed read is logged, never swallowed");
  }

  [Test]
  public async Task Observe_AnInstanceNotInTheFleet_LogsNothing_AndNoReader_IsReportedAsync() {
    var instance = new FixedInstance();
    var reader = new QueuedAssignmentReader();
    var logger = new FakeLogger<InstanceAssignmentMonitor>();
    var observed = new List<InstanceAssignmentObservation>();
    var monitor = InstanceAssignments.Monitor(reader, instance, new FakeTimeProvider(), logger);
    monitor.Observed += observed.Add;
    reader.Answer(InstanceAssignments.Row(Guid.CreateVersion7(), InstanceAssignmentKind.StandardWorker));

    var missing = await monitor.ObserveAsync(CancellationToken.None);

    await Assert.That(missing.Assignment).IsNull();
    await Assert.That(missing.Failure).IsNull();
    await Assert.That(_assigned(logger)).IsEmpty();
    await Assert.That(observed).IsEquivalentTo([missing]);
    var none = await InstanceAssignments.Monitor(null, instance, new FakeTimeProvider()).ObserveAsync(CancellationToken.None);
    await Assert.That(none.Failure).IsEqualTo("no instance-assignment reader is registered (no Postgres driver)");
  }

  [Test]
  public async Task Observe_ACanceledRead_PropagatesAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    var reader = new QueuedAssignmentReader();
    reader.Answer();
    var monitor = InstanceAssignments.Monitor(reader, new FixedInstance(), new FakeTimeProvider());

    await Assert.That(async () => await monitor.ObserveAsync(canceled.Token)).Throws<OperationCanceledException>();
    await Assert.That(monitor.Latest).IsNull();
  }

  [Test]
  public async Task TheGrace_IsTwoSettleWindowsAndARenewInterval_UnlessSetAsync() {
    var options = new InstanceAssignmentOptions();
    await Assert.That(options.ObserveInterval).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.AwaitingShareGrace).IsEqualTo(TimeSpan.Zero);
    await Assert.That(options.GraceFor(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(InstanceAssignments.Monitor(null, new FixedInstance(), new FakeTimeProvider()).AwaitingShareGrace)
      .IsEqualTo((new PartitionAssignerOptions().SettleWindow * 2) + new RoleAssignmentOptions().RenewInterval);

    options.AwaitingShareGrace = TimeSpan.FromMinutes(2);
    await Assert.That(options.GraceFor(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))).IsEqualTo(TimeSpan.FromMinutes(2));
  }

  [Test]
  public async Task TheOptions_RefuseANonPositiveInterval_AndANegativeGrace_AndTheMonitorRefusesNullsAsync() {
    await Assert.That(new InstanceAssignmentOptions { ObserveInterval = TimeSpan.Zero }.Validate).Throws<ArgumentOutOfRangeException>();
    await Assert.That(new InstanceAssignmentOptions { AwaitingShareGrace = TimeSpan.FromSeconds(-1) }.Validate)
      .Throws<ArgumentOutOfRangeException>();
    var instance = new FixedInstance();
    var options = Options.Create(new InstanceAssignmentOptions());
    var assigner = Options.Create(new PartitionAssignerOptions());
    var roles = Options.Create(new RoleAssignmentOptions());
    var logger = new FakeLogger<InstanceAssignmentMonitor>();
    await Assert.That(() => new InstanceAssignmentMonitor(null, null!, options, assigner, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InstanceAssignmentMonitor(null, instance, null!, assigner, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InstanceAssignmentMonitor(null, instance, options, null!, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InstanceAssignmentMonitor(null, instance, options, assigner, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new InstanceAssignmentMonitor(null, instance, options, assigner, roles, null!)).Throws<ArgumentNullException>();
    await Assert.That(new InstanceAssignmentMonitor(null, instance, options, assigner, roles, logger).Latest).IsNull()
      .Because("with no clock given, the system clock is used and nothing is read until asked");
  }
}
