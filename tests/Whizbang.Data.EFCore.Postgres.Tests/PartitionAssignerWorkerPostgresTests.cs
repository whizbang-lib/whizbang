// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The partition assigner's loop against Postgres (#1254): real role elections, real publishes, the announcement over
/// LISTEN/NOTIFY and the claimer's cache. They run hosted workers against a real database, so they are integration
/// tests and live with the other role tests (CommitOrderStamperRoleTests); the loop's own behavior with in-memory fakes
/// is in Whizbang.Core.Component.Tests. Every wait is on the signal for the exact transition asserted (the worker's,
/// the cache's and the shared connection's public signals); the deadline only bounds it.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
[Category("Integration")]
[Category("Shard3")]
public class PartitionAssignerWorkerPostgresTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  private sealed class Pod(Guid id) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = id;
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private WhizbangNotificationOptions _notification() =>
    new() { DirectConnectionString = ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify };

  private static RoleAssignmentOptions _roles() {
    var options = new RoleAssignmentOptions { HoldLegacySessionLock = false, RenewInterval = TimeSpan.FromMilliseconds(250) };
    options.Roles.Add(PartitionAssignerOptions.ROLE);
    return options;
  }

  private PgPartitionAssignmentStore _store() => new(Options.Create(_notification()), _config());

  private PartitionAssignerWorker _workerFor(Pod pod, PartitionAssignerOptions? options = null) {
    var roles = _roles();
    var elector = new PgRoleElector(Options.Create(_notification()), Options.Create(roles), _config(), pod,
      new PgDutyElector(Options.Create(_notification()), _config(), pod, NullLogger<PgDutyElector>.Instance),
      NullLogger<PgRoleElector>.Instance, null);
    return new PartitionAssignerWorker(elector, _store(), NullInstanceConnectionModeSource.Instance, pod,
      Options.Create(options ?? new PartitionAssignerOptions()), Options.Create(roles), NullLogger<PartitionAssignerWorker>.Instance);
  }

  private async Task _registerAsync(Guid instance) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, 'pooled')";
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task<T> _signal<T>(Action<Action<T>> subscribe, Func<T, bool>? when = null) {
    var signal = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscribe(value => {
      if (when is null || when(value)) {
        signal.TrySetResult(value);
      }
    });
    return signal.Task;
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoInstances_OneIsElected_AndPublishesBothAsync(CancellationToken cancellationToken) {
    var podA = new Pod(Guid.CreateVersion7());
    var podB = new Pod(Guid.CreateVersion7());
    await _registerAsync(podA.InstanceId);
    await _registerAsync(podB.InstanceId);
    var a = _workerFor(podA);
    var b = _workerFor(podB);
    var published = new TaskCompletionSource<(PartitionAssignerWorker Worker, PartitionAssignment Assignment)>(
      TaskCreationOptions.RunContinuationsAsynchronously);
    a.OnPublished += assignment => published.TrySetResult((a, assignment));
    b.OnPublished += assignment => published.TrySetResult((b, assignment));

    await a.StartAsync(cancellationToken);
    await b.StartAsync(cancellationToken);
    try {
      var (assigner, assignment) = await published.Task.WaitAsync(_signalDeadline, cancellationToken);

      await Assert.That(assignment.Members.Order()).IsEquivalentTo(new[] { podA.InstanceId, podB.InstanceId }.Order())
        .Because("both instances beat within the pooled window, so the assigner includes both");
      await Assert.That(assigner.IsAssigner).IsTrue();
      await Assert.That((assigner == a ? b : a).IsAssigner).IsFalse().Because("one election, one assigner");
      await Assert.That(assignment.Epoch).IsEqualTo(assigner.Epoch!.Value);
      var read = await _store().ReadAsync(cancellationToken);
      await Assert.That(read!.Assignment).IsEqualTo(assignment);
    } finally {
      await a.StopAsync(CancellationToken.None);
      await b.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TheAssignerStopping_HandsTheRoleOn_AndTheNextPublishesAtANewEpochAsync(CancellationToken cancellationToken) {
    var podA = new Pod(Guid.CreateVersion7());
    var podB = new Pod(Guid.CreateVersion7());
    await _registerAsync(podA.InstanceId);
    var a = _workerFor(podA);
    var firstPublish = _signal<PartitionAssignment>(h => a.OnPublished += h);
    await a.StartAsync(cancellationToken);
    var first = await firstPublish.WaitAsync(_signalDeadline, cancellationToken);

    await _registerAsync(podB.InstanceId);
    var b = _workerFor(podB);
    var aStopped = _signal<long>(h => a.OnStoppedAssigning += h);
    var bPublished = _signal<PartitionAssignment>(h => b.OnPublished += h);
    await b.StartAsync(cancellationToken);
    try {
      // Stopping is the cause, not the wait: the test waits for A's tenure to end and for B's publish.
      await a.StopAsync(CancellationToken.None);
      var endedEpoch = await aStopped.WaitAsync(_signalDeadline, cancellationToken);
      var next = await bPublished.WaitAsync(_signalDeadline, cancellationToken);

      await Assert.That(endedEpoch).IsEqualTo(first.Epoch);
      await Assert.That(next.Epoch).IsGreaterThan(first.Epoch).Because("a new tenure fences with a new epoch");
      await Assert.That(next.Revision).IsEqualTo(1);
      await Assert.That(next.AssignerInstanceId).IsEqualTo(podB.InstanceId);
    } finally {
      await b.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task APublish_IsHeardByTheClaimersCache_WhichThenPresentsTheNewVersionAsync(CancellationToken cancellationToken) {
    var pod = new Pod(Guid.CreateVersion7());
    await _registerAsync(pod.InstanceId);
    var cache = new PartitionAssignmentCache(_store(), NullLogger<PartitionAssignmentCache>.Instance);
    using var shared = new PgSharedNotifyConnection(Options.Create(_notification()), _config(), pod, NullLogger<PgSharedNotifyConnection>.Instance);
    using var subscription = shared.Subscribe(cache);
    await shared.StartAsync(cancellationToken);
    await shared.WaitForChannelListenedAsync(PartitionAssignmentCache.CHANNEL, cancellationToken);
    await Assert.That(await cache.ForClaimAsync(pod.InstanceId, cancellationToken)).IsNull().Because("nothing is published yet");

    var worker = _workerFor(pod);
    var announced = _signal<string>(h => cache.OnPublishAnnounced += h);
    var published = _signal<PartitionAssignment>(h => worker.OnPublished += h);
    await worker.StartAsync(cancellationToken);
    try {
      var assignment = await published.WaitAsync(_signalDeadline, cancellationToken);
      var payload = await announced.WaitAsync(_signalDeadline, cancellationToken);
      var version = await cache.ForClaimAsync(pod.InstanceId, cancellationToken);

      await Assert.That(payload).IsEqualTo($"{assignment.Epoch}:{assignment.Revision}");
      await Assert.That(version).IsEqualTo(assignment.Version);
      await Assert.That(cache.Current).IsEqualTo(assignment);
    } finally {
      await worker.StopAsync(CancellationToken.None);
      await shared.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task SharedConnection_OnADirectConnection_ReportsTheDirectModeAsync(CancellationToken cancellationToken) {
    var pod = new Pod(Guid.CreateVersion7());
    using var shared = new PgSharedNotifyConnection(Options.Create(_notification()), _config(), pod, NullLogger<PgSharedNotifyConnection>.Instance);
    var cache = new PartitionAssignmentCache(_store(), NullLogger<PartitionAssignmentCache>.Instance);
    var subscriber = new PartitionAssignmentSubscriber(shared, cache);
    await Assert.That(shared.ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled).Because("nothing is resolved before it starts");

    await subscriber.StartAsync(cancellationToken);
    await shared.StartAsync(cancellationToken);
    try {
      // The signal: the shared connection has resolved its connection and listens on the assignment channel.
      await shared.WaitForChannelListenedAsync(PartitionAssignmentCache.CHANNEL, cancellationToken).WaitAsync(_signalDeadline, cancellationToken);

      await Assert.That(shared.ConnectionMode).IsEqualTo(InstanceConnectionMode.Direct)
        .Because("a connection of its own, not the pooled fallback, makes the instance's alive-lock visible to its peers");
    } finally {
      await subscriber.StopAsync(CancellationToken.None);
      await subscriber.StopAsync(CancellationToken.None);
      await shared.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task Disabled_TheWorkerNeverVotesAsync(CancellationToken cancellationToken) {
    var pod = new Pod(Guid.CreateVersion7());
    var worker = _workerFor(pod, new PartitionAssignerOptions { Enabled = false });

    await worker.StartAsync(cancellationToken);
    // The signal for this transition: the worker's own task completes when it decides not to run.
    await worker.ExecuteTask!.WaitAsync(_signalDeadline, cancellationToken);

    await Assert.That(worker.IsAssigner).IsFalse();
    await Assert.That(await _store().ReadAsync(cancellationToken)).IsNull();
  }
}
