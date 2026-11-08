// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
using Whizbang.Core.RunControl;
using Whizbang.Core.Serialization;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Versioning;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2022 end to end against a real database: the one verdict (Assess, and the running instance's re-assessment) finds a
/// newer version recorded in the ledger or online on a live instance, by either version, and the stand-down moves the
/// maintenance role on the newer instance's normal vote.
/// </summary>
/// <remarks>
/// "Online" is live by the one liveness rule, not evicted, and serving (its row records AcceptingCommands or Running): a
/// newer instance still starting does not stand the fleet down before it can take the work. Every instance row is
/// written through the database's own registration, aged from the database clock.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStartupAssessment.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupAssessor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StandbyWatcher.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class UpgradeStandDownE2ETests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "upgrade-svc";
    public string HostName => "upgrade-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private static IApplicationVersionProvider _app(string? version) =>
    new ApplicationVersionProvider(ApplicationVersion.FromRecorded(version));

  private EFCorePostgresStartupAssessor _assessor(Pod me, string library = "999.0.0", string? application = "1.0.0") {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    return new EFCorePostgresStartupAssessor(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(library), _app(application), me);
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task _execAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Registers an instance through the database's own heartbeat, as a pooled instance with a 90 second window.</summary>
  private async Task<Guid> _onlineAsync(string library, string? application, string phase, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT record_heartbeat(@id, 'upgrade-svc', 'upgrade-host', 1, '{}'::jsonb, @phase, @library, 90, 'pooled', @application)
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue(nameof(phase), phase);
    cmd.Parameters.AddWithValue(nameof(library), library);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(application), NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)application ?? DBNull.Value });
    await cmd.ExecuteScalarAsync(ct);
    return id;
  }

  /// <summary>Ages an instance's heartbeat past its own window, from the database clock: the rule calls it dead.</summary>
  private Task _dieAsync(Guid id, CancellationToken ct) =>
    _execAsync("UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '10 minutes' WHERE instance_id = @id", ct, ("id", id));

  /// <summary>The library version the test database's ledger was written at.</summary>
  private async Task<string> _ledgerLibraryAsync(CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT v.library_version FROM wh_schema_migrations m JOIN wh_schema_versions v ON v.id = m.version_id
      WHERE m.owner = 'whizbang' ORDER BY m.file_name DESC LIMIT 1
      """;
    return (string)(await cmd.ExecuteScalarAsync(ct))!;
  }

  // ── the verdict ─────────────────────────────────────────────────────

  [Test]
  [Timeout(60000)]
  public async Task ANewerApplicationVersionOnline_StandsAnOlderInstanceDown_NamingTheNewerInstanceAsync(CancellationToken ct) {
    var newer = await _onlineAsync("999.0.0", "2.0.0", "Running", ct);

    var assessment = await _assessor(new Pod()).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains($"instance {newer} is online with application version 2.0.0, newer than this instance's 1.0.0");
  }

  [Test]
  [Timeout(60000)]
  public async Task ANewerLibraryVersionOnline_StandsAnOlderInstanceDownAsync(CancellationToken ct) {
    var newer = await _onlineAsync("1000.0.0", "1.0.0", "Running", ct);

    var assessment = await _assessor(new Pod()).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains($"instance {newer} is online with library version 1000.0.0");
  }

  [Test]
  [Timeout(60000)]
  [Arguments("Migrating")]
  [Arguments("StandingBy")]
  [Arguments("Stopping")]
  public async Task ANewerInstanceThatIsNotServing_IsNotOnlineAsync(string phase, CancellationToken ct) {
    await _onlineAsync("1000.0.0", "2.0.0", phase, ct);

    var assessment = await _assessor(new Pod()).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown)
      .Because("only an instance that can take the work over stands the older ones down: " + assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task ANewerInstanceTheRuleCallsDead_OrOneEvicted_IsNotOnlineAsync(CancellationToken ct) {
    var dead = await _onlineAsync("1000.0.0", "2.0.0", "Running", ct);
    await _dieAsync(dead, ct);
    var evicted = await _onlineAsync("1000.0.0", "2.0.0", "Running", ct);
    await _execAsync("INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason) VALUES (@id, NOW(), 'test')", ct, ("id", evicted));

    var assessment = await _assessor(new Pod()).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task TheInstancesOwnRow_NeverStandsItDownAsync(CancellationToken ct) {
    var me = new Pod();
    await _execAsync("""
      SELECT record_heartbeat(@id, 'upgrade-svc', 'upgrade-host', 1, '{}'::jsonb, 'Running', '1000.0.0', 90, 'pooled', '9.0.0')
      """, ct, ("id", me.InstanceId));

    var assessment = await _assessor(me).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  [Arguments(null, "2.0.0")]
  [Arguments("1.0.0", null)]
  [Arguments("deploy:5", "2.0.0")]
  public async Task AnUnknownOrUnorderedApplicationVersion_StandsNothingDownAsync(string? mine, string? theirs, CancellationToken ct) {
    await _onlineAsync("999.0.0", theirs, "Running", ct);

    var assessment = await _assessor(new Pod(), application: mine).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task ALedgerOnlyReStampedByANewerApplication_StandsNothingDownAsync(CancellationToken ct) {
    // Owner decision 3 (#2022): what stands an instance down is a change applied since its version (RollbackGateTests),
    // not a newer version the ledger merely names. A pass that applied nothing only re-stamps the rows.
    var library = await _ledgerLibraryAsync(ct);
    await _execAsync("""
      WITH v AS (INSERT INTO wh_schema_versions (library_version, application_version) VALUES (@library, '5.0.0') RETURNING id)
      UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v), status = 3
      """, ct, ("library", library));

    var assessment = await _assessor(new Pod(), library).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task ALedgerThatRecordsNoApplicationVersion_IsReadForItsLibraryVersionsAloneAsync(CancellationToken ct) {
    // A ledger written before the application version was recorded: its rows know no application version, which stands
    // nothing down, and the library versions it records still do.
    await _execAsync("ALTER TABLE wh_schema_versions DROP COLUMN application_version CASCADE", ct);
    var library = await _ledgerLibraryAsync(ct);

    await Assert.That((await _assessor(new Pod(), library).AssessAsync(ct)).Verdict).IsNotEqualTo(StartupVerdict.StandDown);
    await Assert.That((await _assessor(new Pod(), "0.0.0-0").AssessAsync(ct)).Verdict).IsEqualTo(StartupVerdict.StandDown);
  }

  [Test]
  [Timeout(60000)]
  public async Task OnADatabaseWithoutTheLivenessRule_OnlyTheLedgerIsReadAsync(CancellationToken ct) {
    // A schema older than the one liveness rule (316): no instance there records an application version, and the read
    // that would judge who is online cannot be made without the rule, so the ledger alone decides.
    await _onlineAsync("1000.0.0", "2.0.0", "Running", ct);
    await _execAsync("DROP FUNCTION wh_live_instance_ids()", ct);
    var assessor = _assessor(new Pod());

    var assessment = await assessor.AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
    await Assert.That(await assessor.ReadOnlineAsync(ct)).IsEmpty();
  }

  [Test]
  [Timeout(60000)]
  public async Task ReadOnline_ReturnsEveryServingLiveInstance_WithBothVersionsAsync(CancellationToken ct) {
    var running = await _onlineAsync("1.0.0", "2.0.0", "Running", ct);
    var accepting = await _onlineAsync("1.0.0", null, "AcceptingCommands", ct);
    await _onlineAsync("1.0.0", "2.0.0", "Migrating", ct);
    var dead = await _onlineAsync("1.0.0", "2.0.0", "Running", ct);
    await _dieAsync(dead, ct);

    var online = await _assessor(new Pod()).ReadOnlineAsync(ct);

    await Assert.That(online).IsEquivalentTo([
      new VersionSighting("1.0.0", "2.0.0", running),
      new VersionSighting("1.0.0", null, accepting),
    ]);
  }

  // ── the stand-down moves the maintenance role ───────────────────────

  private sealed class Bus : ISignalBus {
    private readonly List<(Type Type, Delegate Handler)> _handlers = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
        where TSignal : ISignal => ValueTask.CompletedTask;
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal {
      _handlers.Add((typeof(TSignal), handler));
      return new Subscription();
    }
    public async Task DeliverAsync<TSignal>() where TSignal : ISignal {
      foreach (var (_, handler) in _handlers.Where(h => h.Type == typeof(TSignal))) {
        await ((Func<TSignal, ValueTask>)handler)(default!);
      }
    }
    private sealed class Subscription : ISignalSubscription {
      public void Dispose() { /* the bus is the test's own */ }
    }
  }

  private sealed class Lifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class Host : IHostApplicationLifetime {
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => throw new InvalidOperationException("a stand-down never stops the host");
  }

  private PgRoleElector _elector(Pod pod, WhizbangRunPermit permit) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    Options.Create(new Whizbang.Core.Startup.RoleAssignmentOptions()),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgRoleElector>.Instance,
    libraryVersion: null,
    runPermit: permit);

  [Test]
  [Timeout(120000)]
  public async Task AStoodDownHolder_ReleasesTheMaintenanceRole_TheNewerInstanceTakesItOnItsNextVote_AndAReVoteFollowsItsDeathAsync(
      CancellationToken ct) {
    var old = new Pod();
    await _execAsync("""
      SELECT record_heartbeat(@id, 'upgrade-svc', 'upgrade-host', 1, '{}'::jsonb, 'Running', '999.0.0', 90, 'pooled', '1.0.0')
      """, ct, ("id", old.InstanceId));
    var permit = new WhizbangRunPermit();
    var oldElector = _elector(old, permit);
    var lifecycle = new Lifecycle();
    using var path = new WhizbangRunController(permit, lifecycle, [], [oldElector], new WhizbangLifecycleOptions(), null,
      NullLogger<WhizbangRunController>.Instance);
    var held = await oldElector.TryAcquireAsync(ROLE, ct);
    await Assert.That(held.Grant).IsNotNull();

    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), JsonContextRegistry.CreateCombinedOptions()));
    await using var provider = services.BuildServiceProvider();
    var bus = new Bus();
    using var watcher = new StandbyWatcher(provider.GetRequiredService<IServiceScopeFactory>(), path, new Host(), old,
      new LibraryVersionProvider("999.0.0"), _assessor(old), NullLogger<StandbyWatcher>.Instance, new FakeTimeProvider(),
      options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.FromHours(1) }) {
      Signals = bus,
      Lifecycle = lifecycle,
    };
    await watcher.TickForTestsAsync(ct);
    await Assert.That(path.HaltedFor).IsNull().Because("nothing newer is online yet");

    // The newer instance comes online, and its first vote finds the role held.
    var newer = new Pod();
    await _execAsync("""
      SELECT record_heartbeat(@id, 'upgrade-svc', 'upgrade-host', 1, '{}'::jsonb, 'Running', '999.0.0', 90, 'pooled', '2.0.0')
      """, ct, ("id", newer.InstanceId));
    var newerElector = _elector(newer, new WhizbangRunPermit());
    await Assert.That((await newerElector.TryAcquireAsync(ROLE, ct)).Refusal).IsEqualTo(DutyRefusal.Contended);

    // Its durable announcement reaches the older instance, which stands down through the one path.
    await bus.DeliverAsync<NewerVersionOnlineSignal>();
    await watcher.TickForTestsAsync(ct);
    await Assert.That(path.HaltedFor).IsEqualTo(RunHaltReason.StandDown);
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.StandingBy);

    var moved = await newerElector.TryAcquireAsync(ROLE, ct);
    await Assert.That(moved.Grant).IsNotNull().Because("the role moves on the newer instance's normal vote, with no special hand-off");
    await Assert.That(moved.Grant!.Epoch).IsEqualTo(held.Grant!.Epoch + 1);
    await Assert.That((await oldElector.TryAcquireAsync(ROLE, ct)).Refusal).IsEqualTo(DutyRefusal.Halted);

    // The newer instance dies: its heartbeat and its lease lapse, the older instance resumes, and the role is voted for
    // exactly as when any holder dies.
    await _dieAsync(newer.InstanceId, ct);
    await _execAsync("""
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - INTERVAL '1 hour', renewed_at = renewed_at - INTERVAL '1 hour',
             lease_expires_at = lease_expires_at - INTERVAL '1 hour'
       WHERE role = @role
      """, ct, ("role", ROLE));
    await bus.DeliverAsync<InstanceDiedSignal>();
    await watcher.TickForTestsAsync(ct);

    await Assert.That(path.HaltedFor).IsNull();
    var revoted = await oldElector.TryAcquireAsync(ROLE, ct);
    await Assert.That(revoted.Grant).IsNotNull();
    await revoted.Grant!.DisposeAsync();
  }
}
