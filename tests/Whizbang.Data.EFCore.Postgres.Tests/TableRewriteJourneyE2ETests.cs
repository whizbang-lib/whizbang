using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The rewrite path composed end to end: a migration-style request recorded through the real
/// coordinator, REAL bloat manufactured on a framework table, two instances racing the REAL
/// maintainer election, exactly one executing the REAL <c>VACUUM FULL</c> — and the non-holder
/// skipping, because nobody ever blocks on a rewrite. The pieces (request SQL, candidate
/// re-measurement, step outcomes, duty election) each have their own tests; this proves the
/// journey they compose: requested → measured → elected → rewritten → cleared.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/TableRewriteStartupStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/089_TableRewriteRequests.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class TableRewriteJourneyE2ETests : EFCoreTestBase {

  private sealed class _pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "rewrite-svc";
    public string HostName => "rewrite-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private ServiceProvider _servicesForPod() {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), JsonContextRegistry.CreateCombinedOptions()));
    return services.BuildServiceProvider();
  }

  private PgDutyElector _electorFor(_pod pod) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgDutyElector>.Instance);

  private async Task _joinFleetAsync(_pod pod, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
  }

  /// <summary>Real bloat: fill a framework table, delete most of it, keep it over the candidate
  /// floor — the heap stays sized for the dead rows until something rewrites it.</summary>
  private async Task _manufactureBloatAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using (var fill = conn.CreateCommand()) {
      fill.CommandText = @"
        INSERT INTO wh_settings (setting_key, setting_value, value_type)
        SELECT 'bloat_' || g, repeat('x', 200), 'string' FROM generate_series(1, 20000) g";
      await fill.ExecuteNonQueryAsync(ct);
    }
    await using (var carve = conn.CreateCommand()) {
      carve.CommandText = @"
        DELETE FROM wh_settings
        WHERE setting_key LIKE 'bloat\_%' AND substring(setting_key FROM 7)::INT > 1500";
      await carve.ExecuteNonQueryAsync(ct);
    }
    await using (var analyze = conn.CreateCommand()) {
      analyze.CommandText = "ANALYZE wh_settings";
      await analyze.ExecuteNonQueryAsync(ct);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task RequestedRewrite_TwoPodsRaceTheMaintainerDuty_OneExecutes_TheOtherSkipsAsync(CancellationToken cancellationToken) {
    var podA = new _pod();
    var podB = new _pod();
    await _joinFleetAsync(podA, cancellationToken);
    await _joinFleetAsync(podB, cancellationToken);

    await _manufactureBloatAsync(cancellationToken);
    await using (var requestProvider = _servicesForPod()) {
      await using var scope = requestProvider.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      await coordinator.RequestTableRewriteAsync("wh_settings", cancellationToken);

      var candidates = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken);
      var mine = candidates.Where(c => c.TableName == "wh_settings").ToList();
      await Assert.That(mine).Count().IsEqualTo(1)
        .Because("a genuinely bloated, explicitly requested table is exactly what the step exists for");
      await Assert.That(mine[0].Requested).IsTrue();
    }

    // Two instances, each its own step over its own connections, racing the REAL maintainer
    // election — the post-ready band's arrangement, in miniature.
    var allow = Options.Create(new MaintenanceWorkerOptions { AllowTableRewrite = true });
    await using var providerA = _servicesForPod();
    await using var providerB = _servicesForPod();
    var stepA = new TableRewriteStartupStep(
      providerA.GetRequiredService<IServiceScopeFactory>(), allow);
    var stepB = new TableRewriteStartupStep(
      providerB.GetRequiredService<IServiceScopeFactory>(), allow);
    // Post-ready means AFTER Migrate: each pod carries the full declared chain, its schema gate
    // already open — exactly the state a running instance is in when the rewrite band begins.
    var openGate = new SchemaReadyGate();
    openGate.MarkReady();
    var runnerA = new StartupPipelineRunner(
      [new AssessStartupStep(), new MigrateStartupStep(openGate), stepA], dutyElector: _electorFor(podA));
    var runnerB = new StartupPipelineRunner(
      [new AssessStartupStep(), new MigrateStartupStep(openGate), stepB], dutyElector: _electorFor(podB));

    var results = await Task.WhenAll(
      runnerA.RunAsync(cancellationToken), runnerB.RunAsync(cancellationToken));

    var outcomes = results.Select(r => r.Single(s => s.Name == FrameworkStartupSteps.REWRITE)).ToList();
    var executed = outcomes.Where(o => o.Outcome == StartupStepOutcome.Completed).ToList();
    var skipped = outcomes.Where(o => o.Outcome == StartupStepOutcome.Skipped).ToList();

    await Assert.That(executed).Count().IsEqualTo(1)
      .Because("the maintainer duty is exclusive: exactly one instance takes the VACUUM FULL lock");
    await Assert.That(executed[0].Reason).Contains("rewrote 1 table")
      .Because("the winner did the real rewrite, not a no-op");
    await Assert.That(skipped).Count().IsEqualTo(1);
    await Assert.That(skipped[0].Reason).IsEqualTo("capability not held")
      .Because("nobody blocks on a rewrite — the non-holder skips and carries on");

    // Requested → rewritten → CLEARED: re-measuring offers nothing, and the pending request is gone.
    await using var afterProvider = _servicesForPod();
    await using var afterScope = afterProvider.CreateAsyncScope();
    var after = await afterScope.ServiceProvider.GetRequiredService<IWorkCoordinator>()
      .GetTablesNeedingRewriteAsync(cancellationToken);
    await Assert.That(after.Any(c => c.TableName == "wh_settings")).IsFalse()
      .Because("the rewrite reclaimed the heap; an already-rewritten table is never offered again");
  }
}
