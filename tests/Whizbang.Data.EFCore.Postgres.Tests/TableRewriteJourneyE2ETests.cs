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
    await _waitUntilTheDeletedRowsAreRemovableAsync(conn, ct);
    await using (var analyze = conn.CreateCommand()) {
      analyze.CommandText = "ANALYZE wh_settings";
      await analyze.ExecuteNonQueryAsync(ct);
    }
  }

  /// <summary>
  /// Blocks until PostgreSQL will actually let the carved rows go.
  /// </summary>
  /// <remarks>
  /// <para>Deleting rows does not make them removable. A dead tuple survives until no snapshot
  /// anywhere in the cluster could still see it, and that horizon is held by the oldest running
  /// transaction on the whole server -- including transactions in OTHER databases. Until it
  /// advances past this DELETE, <c>VACUUM FULL</c> copies the dead rows into the new heap and the
  /// table comes out exactly the size it went in.</para>
  ///
  /// <para>That is what made this test flaky on a shared container: with another suite running
  /// against its own database on the same server, the rewrite reclaimed nothing and the step
  /// correctly reported it as ineffective. PostgreSQL says as much when asked --
  /// <c>VACUUM (FULL, VERBOSE)</c> reported "0 removable, 20022 nonremovable row versions" -- and a
  /// second rewrite did no better, because the horizon, not the rewrite, was the problem. Nothing
  /// was wrong with the product: a rewrite that cannot reclaim SHOULD stay queued for the next
  /// boot, which is exactly what it did.</para>
  ///
  /// <para>The snapshot's own xmax after the DELETE is a transaction id no older than it, so once
  /// the cluster's oldest running transaction reaches that mark, every transaction that could
  /// still have seen those rows has finished and they are removable by definition. This polls
  /// because a cleanup horizon is external state with nothing to subscribe to; it is a wait on a
  /// real condition rather than a sleep chosen by feel.</para>
  /// </remarks>
  private static async Task _waitUntilTheDeletedRowsAreRemovableAsync(
      NpgsqlConnection conn, CancellationToken ct) {
    long deletedThrough;
    await using (var mark = conn.CreateCommand()) {
      mark.CommandText = "SELECT pg_snapshot_xmax(pg_current_snapshot())::TEXT::BIGINT";
      deletedThrough = (long)(await mark.ExecuteScalarAsync(ct))!;
    }

    var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
    while (true) {
      long horizon;
      await using (var probe = conn.CreateCommand()) {
        probe.CommandText = "SELECT pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT";
        horizon = (long)(await probe.ExecuteScalarAsync(ct))!;
      }
      if (horizon >= deletedThrough) {
        return;
      }
      if (DateTimeOffset.UtcNow > deadline) {
        throw new InvalidOperationException(
          $"The cluster's cleanup horizon never advanced past the carve (xmin {horizon} still "
          + $"below {deletedThrough}) after 60s, so VACUUM FULL cannot reclaim the deleted rows "
          + "and this test cannot demonstrate a rewrite. A long-running transaction on this "
          + "server -- in any database -- is holding the horizon open.");
      }
      await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
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
    // The non-holder skips for one of two reasons, and which one it gets is a matter of how the
    // two pipelines happen to overlap: denied the duty while the winner still holds it, or granted
    // it afterwards and finding the table already rewritten. Both say the same thing about the
    // product -- it did not perform a second rewrite and it did not block waiting for one -- so
    // pinning the test to whichever the machine produced makes it a timing assertion. Exclusivity
    // itself is not left unguarded: DutyElectionE2ETests covers it directly, twice.
    var nonHolder = skipped[0].Reason switch {
      "capability not held" => "skipped without rewriting",
      "no rewrites owed" => "skipped without rewriting",
      var other => other,
    };
    await Assert.That(nonHolder).IsEqualTo("skipped without rewriting")
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
