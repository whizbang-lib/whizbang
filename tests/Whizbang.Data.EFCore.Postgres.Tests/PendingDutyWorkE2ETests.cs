using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
/// Requirement 7 of #966 against the real database: duty work is "pending until done". A step
/// skipped while an old instance held the duty is owed, and runs once when a new instance wins the
/// role (the rolling-deploy gap). Work interrupted by a hand-off is finished by the next holder, and
/// the interrupted holder can neither write under its stale epoch nor mark the work done. Nothing
/// here sleeps: database time is advanced by shifting stored instants back.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgPendingDutyWorkStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/DutyHolderWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/173_RoleAssignments.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class PendingDutyWorkE2ETests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;
  private static readonly RoleAssignmentOptions _defaults = new();

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "owed-svc";
    public string HostName => "owed-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class RewriteStep : IStartupStep {
    public int Runs { get; private set; }
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Rewrite",
      RequiredCapability = ROLE,
      NonHolderBehavior = NonHolderBehavior.Skip,
      Blocking = false,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Runs++;
      return ValueTask.FromResult(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  private sealed class Handler(Func<IDutyGrant, CancellationToken, ValueTask<DutyWorkResult>> run) : IDutyWorkHandler {
    public string Role => ROLE;
    public string WorkKey => "Repair";
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) => run(grant, cancellationToken);
  }

  private IOptions<WhizbangNotificationOptions> _notification() =>
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString });

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private PgDutyElector _legacyFor(Pod pod) => new(_notification(), _config(), pod, NullLogger<PgDutyElector>.Instance);

  private PgRoleElector _electorFor(Pod pod, bool bridge = true) => new(
    _notification(), Options.Create(new RoleAssignmentOptions { HoldLegacySessionLock = bridge }), _config(), pod, _legacyFor(pod),
    NullLogger<PgRoleElector>.Instance, libraryVersion: null);

  private PgPendingDutyWorkStore _storeFor(Pod pod) =>
    new(_notification(), Options.Create(new RoleAssignmentOptions()), _config(), pod);

  private DutyHolderWorker _workerFor(Pod pod, IDutyElector elector, params IDutyWorkHandler[] handlers) => new(
    elector, _storeFor(pod), handlers, Options.Create(new RoleAssignmentOptions()), NullLogger<DutyHolderWorker>.Instance, notify: null);

  private async Task<Pod> _joinAsync(CancellationToken ct) {
    var pod = new Pod();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    return pod;
  }

  private async Task _executeAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task<long> _countAsync(string sql, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private Task _ageLeaseAsync(CancellationToken ct) => _executeAsync(
    "UPDATE wh_role_assignments SET renewed_at = renewed_at - @by, lease_expires_at = lease_expires_at - @by WHERE role = @role",
    ct, ("by", _defaults.Lease + TimeSpan.FromSeconds(1)), ("role", ROLE));

  [Test]
  [Timeout(120000)]
  public async Task RollingDeploy_AStepSkippedWhileAnOldInstanceHeldTheDuty_RunsOnceWhenANewInstanceWinsItAsync(
      CancellationToken cancellationToken) {
    var old = await _joinAsync(cancellationToken);
    var @new = await _joinAsync(cancellationToken);
    var oldHolding = (await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    // The new instance boots while the old one holds the duty: its step is skipped, and owed.
    var step = new RewriteStep();
    var elector = _electorFor(@new);
    var results = await new StartupPipelineRunner([step], [], elector, _storeFor(@new)).RunAsync(cancellationToken);
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(results[0].Reason).IsEqualTo("capability not held; owed to the duty's holder");

    var worker = _workerFor(@new, elector, new StartupStepDutyWork(step, null));
    await worker.RunOnceAsync(cancellationToken);
    await Assert.That(step.Runs).IsEqualTo(0).Because("the old instance still holds the duty through its session lock");

    // The old instance stops. The new one's next pass wins the role and runs what it was owed.
    await oldHolding.DisposeAsync();
    await worker.RunOnceAsync(cancellationToken);
    await Assert.That(worker.Holds(ROLE)).IsTrue();
    await Assert.That(step.Runs).IsEqualTo(1);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_role_pending_work", cancellationToken)).IsEqualTo(0L);

    await worker.RunOnceAsync(cancellationToken);
    await Assert.That(step.Runs).IsEqualTo(1).Because("done work is no longer owed, so it runs exactly once");
    await worker.ReleaseHeldAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task WorkInterruptedByAHandOff_IsFinishedByTheNextHolder_ExactlyOnceAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    await _executeAsync("CREATE TABLE repair_effects (written_by UUID NOT NULL)", cancellationToken);
    await _storeFor(a).OweAsync(ROLE, "Repair", cancellationToken);

    async Task writeEffectAsync(Pod pod, IDutyGrant grant, CancellationToken ct) =>
      await _executeAsync("SELECT wh_assert_role_epoch(@role, @id, @epoch); INSERT INTO repair_effects VALUES (@id);", ct,
        ("id", pod.InstanceId), ("role", ROLE), ("epoch", grant.Epoch!.Value));

    // Bridge off: while bridged, a stalled holder keeps its session lock and nobody can take over
    // (the documented bridge trade-off), which is not what this test is about.
    var electorB = _electorFor(b, bridge: false);
    var workerB = _workerFor(b, electorB, new Handler(async (grant, ct) => {
      await writeEffectAsync(b, grant, ct);
      return DutyWorkResult.Done();
    }));

    // A holds the role and starts the work; mid-run it stalls past its lease and B takes over.
    var workerA = _workerFor(a, _electorFor(a, bridge: false), new Handler(async (grant, ct) => {
      await _ageLeaseAsync(ct);
      await workerB.RunOnceAsync(ct);
      await writeEffectAsync(a, grant, ct);   // the stale holder wakes and tries to write
      return DutyWorkResult.Done();
    }));
    await workerA.RunOnceAsync(cancellationToken);

    await Assert.That(workerB.Holds(ROLE)).IsTrue();
    await Assert.That(await _countAsync("SELECT count(*) FROM repair_effects", cancellationToken)).IsEqualTo(1L)
      .Because("the stale holder's write was refused by the fence, so the effect happened exactly once");
    await Assert.That(await _countAsync($"SELECT count(*) FROM repair_effects WHERE written_by = '{b.InstanceId}'", cancellationToken))
      .IsEqualTo(1L);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_role_pending_work", cancellationToken)).IsEqualTo(0L);

    await workerA.RunOnceAsync(cancellationToken);
    await Assert.That(workerA.Holds(ROLE)).IsFalse().Because("the interrupted holder finds it no longer holds the role");
    await workerB.ReleaseHeldAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Store_OwesListsCompletesAndRecordsFailures_UnderTheFenceAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var store = _storeFor(a);
    var holder = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await store.OweAsync("host-duty", "HostWork", cancellationToken);
    await Assert.That(await store.ListOwedAsync("host-duty", cancellationToken)).IsEmpty()
      .Because("nothing runs work owed to a duty that is not held by assignment, so none is recorded");

    await store.OweAsync(ROLE, "Rewrite", cancellationToken);
    var owed = (await store.ListOwedAsync(ROLE, cancellationToken)).Single();
    await Assert.That(owed.Role).IsEqualTo(ROLE);
    await Assert.That(owed.IsDue).IsTrue();
    await Assert.That(owed.LastError).IsNull();

    await Assert.That(await store.RecordFailureAsync(owed, holder, "table locked", cancellationToken)).IsTrue();
    var failed = (await store.ListOwedAsync(ROLE, cancellationToken)).Single();
    await Assert.That(failed.Attempts).IsEqualTo(1);
    await Assert.That(failed.LastError).IsEqualTo("table locked");
    await Assert.That(failed.IsDue).IsFalse();

    await store.OweAsync(ROLE, "Rewrite", cancellationToken);
    await Assert.That(await store.CompleteAsync(failed, holder, cancellationToken)).IsEqualTo(DutyWorkCompletion.OwedAgain)
      .Because("it was owed again after it was listed, so the newer need stays");
    var relisted = (await store.ListOwedAsync(ROLE, cancellationToken)).Single();

    // B has not won the role: its grant's epoch is not the holder's, so the fence refuses it.
    var stranger = new StrangerGrant(holder.Epoch!.Value);
    var strangerStore = _storeFor(b);
    await Assert.That(await strangerStore.CompleteAsync(relisted, stranger, cancellationToken)).IsEqualTo(DutyWorkCompletion.Fenced);
    await Assert.That(await strangerStore.RecordFailureAsync(relisted, stranger, "x", cancellationToken)).IsFalse();

    await Assert.That(await store.CompleteAsync(relisted, holder, cancellationToken)).IsEqualTo(DutyWorkCompletion.Completed);
    await Assert.That(await store.ListOwedAsync(ROLE, cancellationToken)).IsEmpty();
    await holder.DisposeAsync();
  }

  private sealed class StrangerGrant(long epoch, bool hasEpoch = true) : IDutyGrant {
    public string Duty => ROLE;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public long? Epoch => hasEpoch ? epoch : null;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  [Test]
  [Timeout(60000)]
  public async Task Store_RefusesBadArguments_AndAGrantWithoutAnEpochAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    var store = _storeFor(pod);
    var work = new PendingDutyWork(ROLE, "Rewrite", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, null, true);

    await Assert.That(async () => await store.OweAsync("", "Rewrite", cancellationToken)).Throws<ArgumentException>();
    await Assert.That(async () => await store.OweAsync(ROLE, "", cancellationToken)).Throws<ArgumentException>();
    await Assert.That(async () => await store.ListOwedAsync("", cancellationToken)).Throws<ArgumentException>();
    await Assert.That(async () => await store.CompleteAsync(work, new StrangerGrant(1, hasEpoch: false), cancellationToken))
      .Throws<ArgumentException>().Because("owed work is fenced by an epoch, and a session-lock grant has none");
    await Assert.That(async () => await store.CompleteAsync(null!, new StrangerGrant(1), cancellationToken)).Throws<ArgumentNullException>();
    await Assert.That(async () => await store.CompleteAsync(work, null!, cancellationToken)).Throws<ArgumentNullException>();
    await Assert.That(async () => await store.RecordFailureAsync(work, new StrangerGrant(1), null!, cancellationToken))
      .Throws<ArgumentNullException>();

    var options = _notification();
    var roles = Options.Create(new RoleAssignmentOptions());
    var config = _config();
    await Assert.That(() => new PgPendingDutyWorkStore(null!, roles, config, pod)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgPendingDutyWorkStore(options, null!, config, pod)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgPendingDutyWorkStore(options, roles, null!, pod)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgPendingDutyWorkStore(options, roles, config, null!)).Throws<ArgumentNullException>();
  }
}
