// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
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

namespace Whizbang.Data.EFCore.Postgres.Tests.Chaos;

/// <summary>
/// The chaos plan of #966 (phase 4), run against the real holder loop, elector, owed-work store and
/// epoch fence: kill the holder, pause it mid-duty, partition it from the database, skew its clock,
/// lose the database for longer than a lease, run a rolling deploy with old and new versions (bridged
/// and not), and start ten instances at once. In every case at most one epoch writes, a new holder
/// appears once the lease lapses in database time and not before, and the owed work completes exactly
/// once.
/// </summary>
/// <remarks>
/// Each simulated instance is its own elector, store and holder loop over its own connections, as
/// separate pods would be. "Time" is database time, advanced by shifting stored instants back; each
/// instance's renew throttle runs on its own fake clock. A pass is driven with
/// <see cref="DutyHolderWorker.RunOnceAsync"/>, and a paused handler waits on a gate the test opens,
/// so nothing sleeps and nothing races. The work writes its effect under the epoch fence, which is
/// what "at most one epoch writes" counts. The database restart is real, in its own server:
/// <see cref="RoleAssignmentDatabaseRestartChaosTests"/>.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/DutyHolderWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/184_RoleAssignmentResilience.sql</code-under-test>
[Category("Integration")]
[Category("Chaos")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentChaosTests : EFCoreTestBase {
  internal const string ROLE = StartupDuties.MAINTAINER;
  internal const string WORK = "Rewrite";
  private static readonly RoleAssignmentOptions _defaults = new();

  [Before(Test)]
  public async Task CreateEffectsTableAsync() =>
    await ChaosPod.ExecuteAsync(ConnectionString, ChaosPod.EFFECTS_DDL, CancellationToken.None);

  private async Task<ChaosPod> _podAsync(
      CancellationToken ct, string? version = null, bool bridge = false, string? connectionString = null, Func<Task>? pause = null,
      FakeTimeProvider? time = null, string[]? extraRoles = null) {
    var pod = await ChaosPod.JoinAsync(ConnectionString, CreateDbContext, ct);
    pod.Build(connectionString ?? ConnectionString, ConnectionString, version, bridge, pause, time ?? new FakeTimeProvider(), extraRoles ?? []);
    return pod;
  }

  private Task _ageAsync(TimeSpan by, CancellationToken ct, string role = ROLE) => ChaosPod.AgeAsync(ConnectionString, role, by, ct);

  private static Task _oweAsync(ChaosPod by, CancellationToken ct) => by.Store.OweAsync(ROLE, WORK, ct);

  private Task<IReadOnlyList<(Guid Holder, long Epoch)>> _effectsAsync(CancellationToken ct) => ChaosPod.EffectsAsync(ConnectionString, ct);

  private static readonly TimeSpan _pastTheLease = _defaults.Lease + TimeSpan.FromSeconds(1);
  private static readonly TimeSpan _withinTheLease = _defaults.Lease - TimeSpan.FromSeconds(5);

  [Test]
  [Timeout(120000)]
  public async Task KillNine_TheHolderVanishes_AndTheNextHolderFinishesTheWorkOnceAsync(CancellationToken cancellationToken) {
    var victim = await _podAsync(cancellationToken);
    var survivor = await _podAsync(cancellationToken);
    await victim.PassAsync(cancellationToken);
    await Assert.That(victim.Worker.Holds(ROLE)).IsTrue();
    await _oweAsync(survivor, cancellationToken);
    // kill -9: the victim never runs again and never releases. It holds no session, so nothing else dies with it.

    await _ageAsync(_withinTheLease, cancellationToken);
    await survivor.PassAsync(cancellationToken);
    await Assert.That(survivor.Worker.Holds(ROLE)).IsFalse().Because("within the lease the dead holder still holds the role");

    await _ageAsync(_pastTheLease - _withinTheLease, cancellationToken);
    await survivor.PassAsync(cancellationToken);

    await Assert.That(survivor.Worker.Holds(ROLE)).IsTrue().Because("one lease after the kill, the next pass takes over");
    var effects = await _effectsAsync(cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1);
    await Assert.That(effects[0].Holder).IsEqualTo(survivor.InstanceId);
    await Assert.That(await survivor.Store.ListOwedAsync(ROLE, cancellationToken)).IsEmpty();
  }

  [Test]
  [Timeout(120000)]
  public async Task Sigstop_TheHolderPausesMidDuty_TheNextHolderFinishes_AndTheWokenHolderIsFencedAsync(CancellationToken cancellationToken) {
    var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stopped = await _podAsync(cancellationToken, pause: async () => {
      paused.TrySetResult();
      await resume.Task;
    });
    var next = await _podAsync(cancellationToken);
    await _oweAsync(next, cancellationToken);

    var stoppedPass = stopped.PassAsync(cancellationToken);   // wins, starts the work, and is paused inside it
    await paused.Task.WaitAsync(cancellationToken);
    await _ageAsync(_pastTheLease, cancellationToken);
    await next.PassAsync(cancellationToken);
    await Assert.That(next.Worker.Holds(ROLE)).IsTrue();

    resume.TrySetResult();   // SIGCONT
    await stoppedPass;

    var effects = await _effectsAsync(cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1).Because("the woken holder's write presented a stale epoch and was refused");
    await Assert.That(effects[0].Holder).IsEqualTo(next.InstanceId);
    await Assert.That(stopped.Worker.Holds(ROLE)).IsFalse().Because("the fence refusal made it let the role go at once");
  }

  [Test]
  [Timeout(120000)]
  public async Task Partition_TheHolderLosesTheDatabase_AnotherTakesOver_AndAfterHealingTheOldHolderCannotWriteAsync(CancellationToken cancellationToken) {
    var target = new NpgsqlConnectionStringBuilder(ConnectionString);
    await using var proxy = new ChaosTcpProxy(target.Host!, target.Port);
    var throughProxy = new NpgsqlConnectionStringBuilder(ConnectionString) {
      Host = "127.0.0.1",
      Port = proxy.Port,
      Pooling = false,
      Timeout = 2,
    }.ConnectionString;
    var partitioned = await _podAsync(cancellationToken, connectionString: throughProxy);
    var other = await _podAsync(cancellationToken);
    await partitioned.PassAsync(cancellationToken);
    var lostEpoch = (await ChaosPod.EpochAsync(ConnectionString, ROLE, cancellationToken))!.Value;
    await _oweAsync(other, cancellationToken);

    proxy.Cut();
    partitioned.Time.Advance(_defaults.RenewInterval);
    await partitioned.PassAsync(cancellationToken);
    await Assert.That(partitioned.Worker.Holds(ROLE)).IsFalse().Because("it cannot renew, so it stops acting");
    await other.PassAsync(cancellationToken);
    await Assert.That(other.Worker.Holds(ROLE)).IsFalse().Because("the row says the partitioned holder's lease still runs");

    await _ageAsync(_pastTheLease, cancellationToken);
    await other.PassAsync(cancellationToken);
    await Assert.That(other.Worker.Holds(ROLE)).IsTrue();

    proxy.Heal();
    await partitioned.PassAsync(cancellationToken);
    await Assert.That(partitioned.Worker.Holds(ROLE)).IsFalse();
    await Assert.That(await ChaosPod.FencedWriteAsync(ConnectionString, partitioned.InstanceId, lostEpoch, cancellationToken)).IsFalse();
    var effects = await _effectsAsync(cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1);
    await Assert.That(effects[0].Holder).IsEqualTo(other.InstanceId);
  }

  [Test]
  [Timeout(120000)]
  public async Task ClockSkew_AnInstanceClockHoursAhead_NeitherExtendsNorShortensTheLeaseAsync(CancellationToken cancellationToken) {
    var skewed = await _podAsync(cancellationToken, time: new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(3)));
    var other = await _podAsync(cancellationToken);
    await skewed.PassAsync(cancellationToken);

    skewed.Time.Advance(TimeSpan.FromDays(1));   // the instance thinks a day went by
    await skewed.PassAsync(cancellationToken);
    var remaining = await ChaosPod.LeaseRemainingAsync(ConnectionString, ROLE, cancellationToken);
    await Assert.That(remaining).IsNotNull();
    await Assert.That(remaining!.Value).IsLessThanOrEqualTo(_defaults.Lease)
      .Because("the renewal extended the lease from the database's now(), never from the instance's clock");

    await _ageAsync(_withinTheLease, cancellationToken);
    await other.PassAsync(cancellationToken);
    await Assert.That(other.Worker.Holds(ROLE)).IsFalse();
    await _ageAsync(_pastTheLease - _withinTheLease, cancellationToken);
    await other.PassAsync(cancellationToken);
    await Assert.That(other.Worker.Holds(ROLE)).IsTrue().Because("the lapse is measured in database time only");
  }

  [Test]
  [Timeout(120000)]
  public async Task DatabaseOutageLongerThanALease_EveryHolderLapses_AndDutiesResumeWithoutTheCooldownAsync(CancellationToken cancellationToken) {
    // Decision 5 of #968. Two roles held by two instances; nobody reaches the database for longer than
    // a lease, so every lease lapses together. When it is back, each holder wins its role straight back.
    var first = await _podAsync(cancellationToken);
    var second = await _podAsync(cancellationToken, extraRoles: ["commit-stamper"]);
    await first.PassAsync(cancellationToken);
    await second.PassAsync(cancellationToken);
    await Assert.That(second.Worker.Holds("commit-stamper")).IsTrue();
    first.Time.Advance(_defaults.RenewInterval);
    await first.PassAsync(cancellationToken);   // both holders renewing: the fleet is healthy up to the outage
    await _oweAsync(first, cancellationToken);

    await _ageAsync(_pastTheLease, cancellationToken);
    await _ageAsync(_pastTheLease, cancellationToken, "commit-stamper");
    first.Time.Advance(_defaults.RenewInterval);
    await first.PassAsync(cancellationToken);

    await Assert.That(first.Worker.Holds(ROLE)).IsTrue()
      .Because("its lapse was fleet-wide, the database's fault, so it is not cooled down");
    var effects = await _effectsAsync(cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1);
  }

  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task RollingDeploy_FromTheSessionLockRelease_NeverHasBothActingAsync(bool bridge, CancellationToken cancellationToken) {
    var oldPod = await ChaosPod.JoinAsync(ConnectionString, CreateDbContext, cancellationToken);
    var lateOldPod = await ChaosPod.JoinAsync(ConnectionString, CreateDbContext, cancellationToken);
    var oldHolder = (await oldPod.LegacyElector(ConnectionString).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var fresh = await _podAsync(cancellationToken, version: "0.2607.0", bridge: bridge);
    await _oweAsync(fresh, cancellationToken);

    await fresh.PassAsync(cancellationToken);
    await Assert.That(fresh.Worker.Holds(ROLE)).IsFalse().Because("a new instance defers to an old session-lock holder");
    await Assert.That(await _effectsAsync(cancellationToken)).IsEmpty();

    await oldHolder.DisposeAsync();   // the old pod stops
    await fresh.PassAsync(cancellationToken);
    await Assert.That(fresh.Worker.Holds(ROLE)).IsTrue();
    await Assert.That((await _effectsAsync(cancellationToken)).Count).IsEqualTo(1);

    // An old pod that starts late (its replacement is still rolling out).
    var late = await lateOldPod.LegacyElector(ConnectionString).TryAcquireAsync(ROLE, cancellationToken);
    if (bridge) {
      await Assert.That(late.Grant).IsNull().Because("bridged, the new holder holds the old lock too, so the old pod defers");
      return;
    }
    // Unbridged, the old pod can take the lock after the vote: the unsafe case the bridge exists for.
    // The new holder sees it on its next renewal and steps aside, so the overlap is one renew interval.
    await Assert.That(late.Grant).IsNotNull();
    fresh.Time.Advance(_defaults.RenewInterval);
    await fresh.PassAsync(cancellationToken);
    await Assert.That(fresh.Worker.Holds(ROLE)).IsFalse();
    await late.Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task RollingDeploy_FromAnOlderRoleAssignmentRelease_DrainsTheOldHolder_AndTheWorkRunsOnceAsync(CancellationToken cancellationToken) {
    // Decisions 2 and 3 of #968: the newer release asks for the role, the old holder finishes its step
    // and releases, and the vacant role goes to the newest version rather than back to an old pod.
    var oldHolder = await _podAsync(cancellationToken, version: "0.2606.0");
    var oldPeer = await _podAsync(cancellationToken, version: "0.2606.0");
    var newPod = await _podAsync(cancellationToken, version: "0.2607.0");
    await oldHolder.PassAsync(cancellationToken);
    await oldPeer.PassAsync(cancellationToken);

    await newPod.PassAsync(cancellationToken);   // asks for a drain
    oldHolder.Time.Advance(_defaults.RenewInterval);
    await oldHolder.PassAsync(cancellationToken);   // learns it on the renewal, releases
    await Assert.That(oldHolder.Worker.Holds(ROLE)).IsFalse();
    await _oweAsync(newPod, cancellationToken);
    await oldPeer.PassAsync(cancellationToken);
    await Assert.That(oldPeer.Worker.Holds(ROLE)).IsFalse().Because("the newer candidate is preferred for the vacant role");

    await newPod.PassAsync(cancellationToken);
    await Assert.That(newPod.Worker.Holds(ROLE)).IsTrue();
    var effects = await _effectsAsync(cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1);
    await Assert.That(effects[0].Holder).IsEqualTo(newPod.InstanceId);
  }

  [Test]
  [Timeout(120000)]
  public async Task TenInstancesStartAtOnce_ExactlyOneHolds_AndTheWorkRunsOnceAsync(CancellationToken cancellationToken) {
    var pods = new List<ChaosPod>();
    for (var i = 0; i < 10; i++) {
      pods.Add(await _podAsync(cancellationToken));
    }
    await _oweAsync(pods[0], cancellationToken);

    await Task.WhenAll(pods.Select(pod => pod.PassAsync(cancellationToken)));
    await Task.WhenAll(pods.Select(pod => pod.PassAsync(cancellationToken)));

    await Assert.That(pods.Count(pod => pod.Worker.Holds(ROLE))).IsEqualTo(1);
    var effects = await _effectsAsync(cancellationToken);
    await Assert.That(effects.Count).IsEqualTo(1);
    await Assert.That(effects.Select(e => e.Epoch).Distinct().Count()).IsEqualTo(1);
  }
}

/// <summary>One simulated instance: its elector, owed-work store and holder loop, with a fenced unit of work.</summary>
internal sealed class ChaosPod : IServiceInstanceProvider {
  public const string EFFECTS_DDL = """
    CREATE TABLE IF NOT EXISTS chaos_effects (
      work_key TEXT NOT NULL, holder UUID NOT NULL, epoch BIGINT NOT NULL, at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp())
    """;

  public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
  public string ServiceName => "chaos-svc";
  public string HostName => "chaos-host";
  public int ProcessId => 1;
  public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };

  public FakeTimeProvider Time { get; private set; } = null!;
  public PgPendingDutyWorkStore Store { get; private set; } = null!;
  public DutyHolderWorker Worker { get; private set; } = null!;

  public static async Task<ChaosPod> JoinAsync(string connectionString, Func<WorkCoordinationDbContext> context, CancellationToken ct) {
    var pod = new ChaosPod();
    await using var ctx = context();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    return pod;
  }

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  public PgDutyElector LegacyElector(string connectionString) =>
    new(Options.Create(new WhizbangNotificationOptions { DirectConnectionString = connectionString }), _config(), this, NullLogger<PgDutyElector>.Instance);

  public void Build(
      string podConnection, string effectsConnection, string? version, bool bridge, Func<Task>? pause, FakeTimeProvider time,
      IEnumerable<string> extraRoles, RoleAssignmentOptions? tuned = null) {
    Time = time;
    var options = tuned ?? new RoleAssignmentOptions();
    options.HoldLegacySessionLock = bridge;
    foreach (var role in extraRoles) {
      options.Roles.Add(role);
    }
    var notification = Options.Create(new WhizbangNotificationOptions { DirectConnectionString = podConnection });
    var elector = new PgRoleElector(notification, Options.Create(options), _config(), this, LegacyElector(podConnection),
      NullLogger<PgRoleElector>.Instance, version is null ? null : new LibraryVersionProvider(version), timeProvider: time);
    Store = new PgPendingDutyWorkStore(notification, Options.Create(options), _config(), this);
    IDutyWorkHandler[] handlers = [
      new EffectWork(RoleAssignmentChaosTests.ROLE, RoleAssignmentChaosTests.WORK, effectsConnection, this, pause),
      .. extraRoles.Select(role => (IDutyWorkHandler)new EffectWork(role, "Idle", effectsConnection, this, null)),
    ];
    Worker = new DutyHolderWorker(elector, Store, handlers, Options.Create(options), NullLogger<DutyHolderWorker>.Instance, null);
  }

  public Task PassAsync(CancellationToken ct) => Worker.RunOnceAsync(ct);

  /// <summary>The unit of duty work: one fenced write per run, in the same transaction as the epoch check.</summary>
  private sealed class EffectWork(string role, string key, string connectionString, ChaosPod pod, Func<Task>? pause) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => key;

    public async ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) {
      if (pause is not null) {
        await pause();
      }
      await using var conn = new NpgsqlConnection(connectionString);
      await conn.OpenAsync(cancellationToken);
      await using var tx = await conn.BeginTransactionAsync(cancellationToken);
      try {
        await PgRoleElector.AssertEpochAsync(conn, grant, cancellationToken);
      } catch (PostgresException ex) when (ex.SqlState == "WHF01") {
        return DutyWorkResult.NotDone("fenced: this instance no longer holds the role");
      }
      await using var insert = conn.CreateCommand();
      insert.CommandText = "INSERT INTO chaos_effects (work_key, holder, epoch) VALUES (@key, @holder, @epoch)";
      insert.Parameters.AddWithValue(nameof(key), key);
      insert.Parameters.AddWithValue("holder", pod.InstanceId);
      insert.Parameters.AddWithValue("epoch", grant.Epoch!.Value);
      _ = await insert.ExecuteNonQueryAsync(cancellationToken);
      await tx.CommitAsync(cancellationToken);
      return DutyWorkResult.Done();
    }
  }

  public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync(ct);
  }

  public static Task AgeAsync(string connectionString, string role, TimeSpan by, CancellationToken ct) => ExecuteAsync(connectionString, """
    WITH a AS (
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role RETURNING 1)
    UPDATE wh_role_candidates SET last_voted_at = last_voted_at - @by WHERE role = @role
    """, ct, ("by", by), ("role", role));

  public static async Task<IReadOnlyList<(Guid Holder, long Epoch)>> EffectsAsync(string connectionString, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT holder, epoch FROM chaos_effects WHERE work_key = @key ORDER BY at";
    cmd.Parameters.AddWithValue("key", RoleAssignmentChaosTests.WORK);
    var effects = new List<(Guid, long)>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      effects.Add((reader.GetGuid(0), reader.GetInt64(1)));
    }
    return effects;
  }

  public static async Task<long?> EpochAsync(string connectionString, string role, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT epoch FROM wh_role_assignments WHERE role = @role";
    cmd.Parameters.AddWithValue(nameof(role), role);
    return await cmd.ExecuteScalarAsync(ct) as long?;
  }

  public static async Task<TimeSpan?> LeaseRemainingAsync(string connectionString, string role, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT lease_remaining FROM wh_role_assignment_status() WHERE role = @role";
    cmd.Parameters.AddWithValue(nameof(role), role);
    return await cmd.ExecuteScalarAsync(ct) as TimeSpan?;
  }

  public static async Task<bool> FencedWriteAsync(string connectionString, Guid instanceId, long epoch, CancellationToken ct) {
    try {
      await ExecuteAsync(connectionString, "SELECT wh_assert_role_epoch(@role, @id, @epoch)", ct,
        ("role", RoleAssignmentChaosTests.ROLE), ("id", instanceId), ("epoch", epoch));
      return true;
    } catch (PostgresException ex) when (ex.SqlState == "WHF01") {
      return false;
    }
  }
}
