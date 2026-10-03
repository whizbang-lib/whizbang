using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The commit-order stamper's leadership as a role (#966 phase 3): one holder per schema, won by a
/// vote, kept by the stamping loop's own verification, every stamp fenced by the holder's epoch, a
/// newer-version instance handed the role after the stamp in progress, and a waiting non-holder
/// woken by the role's release. Waits are on the worker's own events; the renew throttle runs on a
/// fake clock, so a leader's view of its role changes only when a test says so.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class CommitOrderStamperRoleTests : EFCoreTestBase {
  private const string ROLE = CommitOrderStamperOptions.ROLE;
  private static readonly RoleAssignmentOptions _defaults = new();

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "stamp-svc";
    public string HostName => "stamp-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>Completes when the worker logs the given event id.</summary>
  private sealed class SignalingLogger : ILogger<PgCommitOrderStamperWorker> {
    private readonly int _eventId;
    public SignalingLogger(int eventId) => _eventId = eventId;
    public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (eventId.Id == _eventId) {
        Logged.TrySetResult();
      }
    }
  }

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private WhizbangNotificationOptions _notification(string? connectionString = null) =>
    new() { DirectConnectionString = connectionString ?? ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify };

  private PgRoleElector _electorFor(Pod pod, string? version = null, TimeProvider? time = null, string? connectionString = null) {
    var options = new RoleAssignmentOptions { HoldLegacySessionLock = false };
    options.Roles.Add(ROLE);
    return new PgRoleElector(Options.Create(_notification(connectionString)), Options.Create(options), _config(), pod,
      new PgDutyElector(Options.Create(_notification(connectionString)), _config(), pod, NullLogger<PgDutyElector>.Instance),
      NullLogger<PgRoleElector>.Instance, version is null ? null : new LibraryVersionProvider(version), timeProvider: time);
  }

  private PgSharedNotifyConnection _sharedFor(Pod pod) =>
    new(Options.Create(_notification()), _config(), pod, NullLogger<PgSharedNotifyConnection>.Instance);

  private PgCommitOrderStamperWorker _workerFor(
      Pod pod, IDutyElector elector, ISharedNotifyConnection? shared = null, ILogger<PgCommitOrderStamperWorker>? logger = null,
      TimeSpan? polling = null, TimeSpan? retry = null, INotificationDataSource? dataSource = null) => new(
    Options.Create(_notification()),
    Options.Create(new CommitOrderStamperOptions {
      PollingInterval = polling ?? TimeSpan.FromMilliseconds(100),
      LeaderElectionRetry = retry ?? TimeSpan.FromMinutes(10),
      BatchSize = 100,
    }),
    _config(),
    shared ?? _sharedFor(pod),
    logger ?? NullLogger<PgCommitOrderStamperWorker>.Instance,
    elector,
    notificationDataSource: dataSource);

  private async Task<Pod> _joinAsync(CancellationToken ct) {
    var pod = new Pod();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    return pod;
  }

  private static TaskCompletionSource _when(Action<Action> subscribe) {
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    subscribe(() => tcs.TrySetResult());
    return tcs;
  }

  private async Task<object?> _scalarAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is DBNull ? null : result;
  }

  private Task _ageAsync(TimeSpan by, CancellationToken ct) => _scalarAsync("""
    UPDATE wh_role_assignments
       SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by, lease_expires_at = lease_expires_at - @by
     WHERE role = @role
    """, ct, ("by", by), ("role", ROLE));

  private async Task<Guid> _insertEventAsync(CancellationToken ct) {
    var eventId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    _ = await _scalarAsync("""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      VALUES (@eid, @sid, @sid, 'TestAggregate', 1, 'TestEvent', NULL, NOW())
      """, ct, ("eid", eventId), ("sid", streamId));
    return eventId;
  }

  private async Task<Guid?> _holderAsync(CancellationToken ct) =>
    await _scalarAsync("SELECT holder_instance_id FROM wh_role_assignments WHERE role = @role", ct, ("role", ROLE)) as Guid?;

  [Test]
  [Timeout(120000)]
  public async Task TheRoleHolder_StampsUnderTheFence_AndIsTheOnlyLeaderAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    await using var dataSource = new NotificationDataSource(new NpgsqlDataSourceBuilder(ConnectionString).Build());
    var leaderA = _workerFor(a, _electorFor(a), dataSource: dataSource);
    var follower = _workerFor(b, _electorFor(b));
    var led = _when(h => leaderA.OnBecameLeader += h);
    var stamped = _when(h => leaderA.OnStampCompleted += n => { if (n > 0) { h(); } });

    await leaderA.StartAsync(cancellationToken);
    await led.Task.WaitAsync(cancellationToken);
    await follower.StartAsync(cancellationToken);
    var eventId = await _insertEventAsync(cancellationToken);
    await stamped.Task.WaitAsync(cancellationToken);

    await Assert.That(await _scalarAsync("SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", cancellationToken, ("id", eventId)))
      .IsNotNull();
    await Assert.That(await _holderAsync(cancellationToken)).IsEqualTo(a.InstanceId);
    await Assert.That(follower.IsLeader).IsFalse().Because("the vote gives the role to one instance per schema");
    await leaderA.StopAsync(CancellationToken.None);
    await follower.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(120000)]
  public async Task ALeaderThatLostTheRole_IsRefusedByTheFenceAtItsNextStamp_AndStopsLeadingAsync(CancellationToken cancellationToken) {
    // Requirement 1: its throttled verify still answers from memory, but the database refuses its epoch.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var fenced = new SignalingLogger(eventId: 17);
    var stale = _workerFor(a, _electorFor(a, time: new FakeTimeProvider()), logger: fenced);
    var led = _when(h => stale.OnBecameLeader += h);
    var stopped = _when(h => stale.OnStoppedLeading += h);
    await stale.StartAsync(cancellationToken);
    await led.Task.WaitAsync(cancellationToken);

    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var takeover = (await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var eventId = await _insertEventAsync(cancellationToken);
    await fenced.Logged.Task.WaitAsync(cancellationToken);
    await stopped.Task.WaitAsync(cancellationToken);

    await Assert.That(await _scalarAsync("SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", cancellationToken, ("id", eventId)))
      .IsNull().Because("the fenced stamp rolled back with the epoch check");
    await Assert.That(await _holderAsync(cancellationToken)).IsEqualTo(b.InstanceId);
    await stale.StopAsync(CancellationToken.None);
    await takeover.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task ALeaderWhoseRenewalIsRefused_StopsLeadingAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var time = new FakeTimeProvider();
    var lost = new SignalingLogger(eventId: 15);
    var leader = _workerFor(a, _electorFor(a, time: time), logger: lost, polling: TimeSpan.FromMinutes(1));
    var led = _when(h => leader.OnBecameLeader += h);
    await leader.StartAsync(cancellationToken);
    await led.Task.WaitAsync(cancellationToken);

    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var takeover = (await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    time.Advance(_defaults.RenewInterval);
    leader.Wake();
    await lost.Logged.Task.WaitAsync(cancellationToken);

    await Assert.That(await _holderAsync(cancellationToken)).IsEqualTo(b.InstanceId);
    await leader.StopAsync(CancellationToken.None);
    await takeover.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task ALeaderAskedToDrain_ReleasesTheRole_AndTheNewerInstanceTakesItAsync(CancellationToken cancellationToken) {
    // Decision 2 of #968, for the stamper. The polling interval is longer than a renew interval, so the
    // loop's wait is capped at the renew interval: its own verification is what keeps the lease.
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var time = new FakeTimeProvider();
    var drained = new SignalingLogger(eventId: 16);
    var leader = _workerFor(older, _electorFor(older, "0.2606.0", time), logger: drained, polling: TimeSpan.FromMinutes(1));
    var led = _when(h => leader.OnBecameLeader += h);
    var stopped = _when(h => leader.OnStoppedLeading += h);
    await leader.StartAsync(cancellationToken);
    await led.Task.WaitAsync(cancellationToken);

    var asked = await _electorFor(newer, "0.2607.0").TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(asked.Detail).Contains("drain");
    time.Advance(_defaults.RenewInterval);
    leader.Wake();
    await drained.Logged.Task.WaitAsync(cancellationToken);
    await stopped.Task.WaitAsync(cancellationToken);

    var handedOver = await _electorFor(newer, "0.2607.0").TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(handedOver.Grant).IsNotNull()
      .Because($"the older leader released, and its next vote defers to the newer candidate. {handedOver.Detail}");
    await leader.StopAsync(CancellationToken.None);
    await handedOver.Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AWaitingNonHolder_IsWokenByTheRolesRelease_NotItsRetryIntervalAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var leader = _workerFor(a, _electorFor(a));
    var led = _when(h => leader.OnBecameLeader += h);
    await leader.StartAsync(cancellationToken);
    await led.Task.WaitAsync(cancellationToken);
    var shared = _sharedFor(b);
    await shared.StartAsync(cancellationToken);
    var waiter = _workerFor(b, _electorFor(b), shared, retry: TimeSpan.FromHours(1));
    var waiterLed = _when(h => waiter.OnBecameLeader += h);
    await waiter.StartAsync(cancellationToken);
    await shared.WaitForChannelListenedAsync(DutyHolderWorker.RELEASE_CHANNEL, cancellationToken);
    _ = await _scalarAsync("SELECT pg_notify('wh_role_released', 'maintainer')", cancellationToken);

    await leader.StopAsync(CancellationToken.None);
    await waiterLed.Task.WaitAsync(cancellationToken);

    await Assert.That(await _holderAsync(cancellationToken)).IsEqualTo(b.InstanceId)
      .Because("the release was announced, so the waiter voted at once instead of after its hour-long retry");
    await waiter.StopAsync(CancellationToken.None);
    await shared.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(120000)]
  public async Task AVoteThatCannotRun_IsLogged_AndTheWorkerStillStopsCleanlyAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var failed = new SignalingLogger(eventId: 6);
    var unreachable = new NpgsqlConnectionStringBuilder(ConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
    var worker = _workerFor(a, _electorFor(a, connectionString: unreachable), logger: failed);

    await worker.StartAsync(cancellationToken);
    await failed.Logged.Task.WaitAsync(cancellationToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.IsLeader).IsFalse();
  }
}
