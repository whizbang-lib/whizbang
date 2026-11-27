// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.StartupParity;

/// <summary>
/// #2596 on both drivers alike: the Converge stage's heavy work runs through one session. The first start of a build
/// records its shared work done, a restart of the same build skips it, every permit taken is given back, and a start
/// past the fleet-wide cap waits in Converge for a permit, says so, and goes on once one is free.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/StartupWorkSessions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
public partial class StartupParityTests {
  private const string CAP_KEY = "Whizbang:Database:StartupWork:MaxConcurrentHeavyStarts";
  private const string POLL_KEY = "Whizbang:Database:StartupWork:PermitPollInterval";

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task TheFirstStart_RecordsItsSharedWorkDone_ARestartOfTheBuildSkipsIt_AndNoPermitStaysHeldAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    var first = await _startAsync(driver);
    var firstId = first.GetRequiredService<IServiceInstanceProvider>().InstanceId;

    var recorded = await _startupWorkAsync();
    await Assert.That(recorded.Keys).IsEquivalentTo([
      FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, FrameworkStartupWork.PERSPECTIVE_REGISTRY,
      FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS,
    ]).Because("only shared work is recorded, the managed-object reconcile among it (#2608); per-instance work runs at every start");
    await Assert.That(recorded.Values.All(r => r.State == "done" && r.Instance == firstId)).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_startup_permits")).IsEqualTo(0L)
      .Because("the permit the start took is given back when its Converge work is done");

    var second = await _startAsync(driver);

    var after = await _startupWorkAsync();
    await Assert.That(after.Values.All(r => r.State == "done" && r.Instance == firstId)).IsTrue()
      .Because("the restart of the same build found the shared work done and skipped it");
    await Assert.That(second.GetRequiredService<IServiceInstanceProvider>().InstanceId).IsNotEqualTo(firstId);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_startup_permits")).IsEqualTo(0L);
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task PastTheCap_AStartWaitsInConvergeForAPermit_AndGoesOnOnceOneIsFreeAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    _ = await _startAsync(driver);
    var peer = await _livePeerHoldingAPermitAsync();
    // A rollout of the next build, when the cap matters: the start owes the shared reconcile, so it asks for a permit.
    await _asIfTheNextBuildStartsAsync();
    var stages = new PermitWaitStages();
    var instance = _compose(driver, true, null, null, s => s.AddSingleton<IStartupStageReporter>(stages),
      (CAP_KEY, "1"), (POLL_KEY, "00:00:00.050"));

    var start = _initializer(instance).StartAsync(cancellationToken);
    await stages.Waiting.Task.WaitAsync(cancellationToken);
    await Assert.That(start.IsCompleted).IsFalse().Because("the cap is held by a live peer, so the start waits");

    await _execAsync($"DELETE FROM wh_startup_permits WHERE instance_id = '{peer}'");
    await start;

    await Assert.That(stages.WaitingMessage).IsEqualTo("waiting for a heavy-start permit (1 of 1 held across the fleet)");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_startup_permits")).IsEqualTo(0L);
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task TheCap_BindsFromConfiguration_AndKeepsItsDefaultsWithoutItAsync(string driverName) {
    var driver = StartupDriver.For(driverName);

    var configured = _compose(driver, true, null, null, null,
      (CAP_KEY, "5"), (POLL_KEY, "00:00:02"), ("Whizbang:Database:StartupWork:PermitWaitLimit", "00:01:00"))
      .GetRequiredService<IOptions<StartupWorkOptions>>().Value;
    var defaults = _compose(driver).GetRequiredService<IOptions<StartupWorkOptions>>().Value;

    await Assert.That(configured.MaxConcurrentHeavyStarts).IsEqualTo(5);
    await Assert.That(configured.PermitPollInterval).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(configured.PermitWaitLimit).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(defaults.MaxConcurrentHeavyStarts).IsEqualTo(StartupWorkOptions.DEFAULT_MAX_CONCURRENT_HEAVY_STARTS);
    await Assert.That(defaults.PermitPollInterval).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(defaults.PermitWaitLimit).IsEqualTo(TimeSpan.FromMinutes(5));
  }

  private async Task<Dictionary<string, (string State, Guid Instance)>> _startupWorkAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT work_name, state, instance_id FROM wh_startup_work", db);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new Dictionary<string, (string, Guid)>(StringComparer.Ordinal);
    while (await reader.ReadAsync()) {
      rows[reader.GetString(0)] = (reader.GetString(1), reader.GetGuid(2));
    }
    return rows;
  }

  /// <summary>A live registered peer (a fresh heartbeat on the database clock) that holds a heavy-start permit.</summary>
  private async Task<Guid> _livePeerHoldingAPermitAsync() {
    var peer = (Guid)TrackedGuid.New();
    await _execAsync($"""
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode,
         liveness_window_seconds, startup_stage, lifecycle_phase, metadata, library_version)
      VALUES ('{peer}', 'peer-svc', 'peer-host', 1, NOW(), NOW(), 'pooled', 600, 40, 'Migrating', jsonb_build_object(), '1.2.3');
      INSERT INTO wh_startup_permits (instance_id) VALUES ('{peer}');
      """);
    return peer;
  }

  /// <summary>Signals the first time a start says it waits for a heavy-start permit.</summary>
  private sealed class PermitWaitStages : IStartupStageReporter {
    private readonly Lock _lock = new();
    private StartupStageSnapshot? _current;

    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? WaitingMessage { get; private set; }
    public StartupStageSnapshot? Current { get { lock (_lock) { return _current; } } }

    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _current = new StartupStageSnapshot(stage, message, false, DateTimeOffset.UnixEpoch);
      }
      if (stage == StartupStage.Converge && message.StartsWith("waiting for a heavy-start permit", StringComparison.Ordinal)) {
        WaitingMessage = message;
        Waiting.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _current = new StartupStageSnapshot(stage, "skipped: " + reason, true, DateTimeOffset.UnixEpoch);
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }
}
