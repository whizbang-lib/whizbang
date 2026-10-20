// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Perspective row retention runs on the Dapper driver: the maintenance cycle's expiry sweep, cap sweep, adoption gate,
/// pre-destruction seam and stream-group cascade drain reach the same shared SQL the EF Core driver's do (#1505).
/// </summary>
/// <remarks>
/// The Dapper coordinator implemented none of these and inherited the interface's defaults: an "unsupported" sweep, an
/// empty journal, nothing adopted. A Dapper host that declared <c>[RowTtl]</c> or <c>[RowCap]</c> kept every row forever.
/// Each test here has a twin on the EF Core driver (<c>PerspectiveRowDestructionSeamSqlTests</c>,
/// <c>StreamGroupCascadeSqlTests</c>, <c>RetentionAutoAdoptionTests</c>).
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PerspectiveRowRetentionSql.cs</code-under-test>
[Category("Integration")]
public class DapperRowRetentionTests : PostgresTestBase {
  private const string LEADER_TABLE = "wh_per_dapper_leader";
  private const string FOLLOWER_TABLE = "wh_per_dapper_follower";
  private const string ROW_CAP_KEY = "row_cap_sweep_last_run";

  private sealed class DapperLeaderModel;
  private sealed class DapperFollowerModel;

  private static string _leaderClr => typeof(DapperLeaderModel).FullName!;
  private static string _followerClr => typeof(DapperFollowerModel).FullName!;

  private readonly WarningLog _log = new();

  private DapperWorkCoordinator _coordinator() =>
    new(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), _log);

  [Before(Test)]
  public async Task ArrangeAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync($@"
      CREATE TABLE {LEADER_TABLE} (
        id UUID NOT NULL PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ, expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      CREATE TABLE {FOLLOWER_TABLE} (LIKE {LEADER_TABLE} INCLUDING ALL);
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@leader, '{LEADER_TABLE}', '{{}}'::jsonb, 'h', 'svc'), (@follower, '{FOLLOWER_TABLE}', '{{}}'::jsonb, 'h', 'svc');",
      new { leader = _leaderClr, follower = _followerClr });
  }

  [Test]
  public async Task TheMaintenanceCycle_AdoptsTheDeclaredWindow_SweepsExpiredRows_AndCascadesToTheGroupAsync() {
    var expired = Guid.CreateVersion7();
    var live = Guid.CreateVersion7();
    await _seedAsync(LEADER_TABLE, expired, idleHours: 48);
    await _seedAsync(FOLLOWER_TABLE, expired, idleHours: 0);
    await _seedAsync(LEADER_TABLE, live, idleHours: 0);
    await _seedAsync(FOLLOWER_TABLE, live, idleHours: 0);
    await _coordinator().SyncPerspectiveRetentionAsync([_declaration(_leaderClr, ttlSeconds: 3600)]);

    PerspectiveStreamGroupRegistry.Register(typeof(DapperLeaderModel), "dapper-sg", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(DapperFollowerModel), "dapper-sg", announce: true, follow: true, bridge: false);
    try {
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(_worker());
    } finally {
      Whizbang.Testing.StreamGroupRegistryTestSeam.Clear();
    }

    await Assert.That(await _survivesAsync(LEADER_TABLE, expired)).IsFalse()
      .Because("the declared one-hour window is adopted and swept in the same cycle; on this driver the sweep never ran");
    await Assert.That(await _survivesAsync(FOLLOWER_TABLE, expired)).IsFalse()
      .Because("the follower has no evictor of its own, so only the cascade drain can have taken it");
    await Assert.That(await _survivesAsync(LEADER_TABLE, live)).IsTrue();
    await Assert.That(await _survivesAsync(FOLLOWER_TABLE, live)).IsTrue();
    await Assert.That(await _scalarAsync<bool>(
      "SELECT retention_enforcement_acknowledged FROM wh_perspective_registry WHERE clr_type_name = @c", new { c = _leaderClr })).IsTrue();
  }

  [Test]
  public async Task TheMaintenanceCycle_OffersExpiringRowsToTheGuard_AndADeferHoldsThemAsync() {
    var expired = Guid.CreateVersion7();
    var unscoped = Guid.CreateVersion7();
    await _seedAsync(LEADER_TABLE, expired, idleHours: 48);
    await _seedAsync(LEADER_TABLE, unscoped, idleHours: 48, user: null);
    await _coordinator().SyncPerspectiveRetentionAsync([_declaration(_leaderClr, ttlSeconds: 3600)]);
    await _coordinator().AcknowledgeRetentionEnforcementAsync(_leaderClr);
    var guard = new LeaderGuard { Clean = false };

    var worker = _worker(guard);
    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(guard.Offered).Contains(expired)
      .Because("the seam's collect phase reads what the sweep would destroy; on this driver it read nothing");
    await Assert.That(guard.Scopes[expired]!.Value.GetProperty("u").GetString()).IsEqualTo("user-0");
    await Assert.That(guard.Scopes[unscoped]).IsNull()
      .Because("a row with no scope is offered with none, not with an empty document");
    await Assert.That(await _survivesAsync(LEADER_TABLE, expired)).IsTrue()
      .Because("a guard's Defer is a durable hold the sweep honors");

    guard.Clean = true;
    await _executeAsync("UPDATE wh_perspective_row_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE table_name = @t", new { t = LEADER_TABLE });
    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker);
    await Assert.That(await _survivesAsync(LEADER_TABLE, expired)).IsFalse();
    await Assert.That(guard.Released).Contains(expired);
    await Assert.That(await _survivesAsync(LEADER_TABLE, unscoped)).IsFalse();
  }

  [Test]
  public async Task TheCapSweep_IsClaimedOncePerWindow_AndEvictsTheOverflowAsync() {
    var newest = Guid.CreateVersion7();
    var overflow = Guid.CreateVersion7();
    await _seedAsync(LEADER_TABLE, newest, idleHours: 1, user: "user-1");
    await _seedAsync(LEADER_TABLE, overflow, idleHours: 10, user: "user-1");
    var coordinator = _coordinator();
    await coordinator.SyncPerspectiveRetentionAsync([_declaration(_leaderClr, ttlSeconds: null, cap: 1)]);
    await Assert.That(await _scalarAsync<int?>(
      "SELECT row_cap_per_scope FROM wh_perspective_registry WHERE clr_type_name = @c", new { c = _leaderClr })).IsEqualTo(1);

    await Assert.That(await coordinator.TryClaimRowCapSweepAsync(TimeSpan.FromHours(1))).IsTrue();
    await Assert.That(await coordinator.TryClaimRowCapSweepAsync(TimeSpan.FromHours(1))).IsFalse()
      .Because("the watermark is first-instance-wins: a sibling inside the window skips");
    // The cap sweep honors the adoption gate as the expiry sweep does; in a maintenance cycle adoption opens it.
    await coordinator.AcknowledgeRetentionEnforcementAsync(_leaderClr);
    var result = await coordinator.ReapPerspectiveRowCapsAsync();

    await Assert.That(result.RowsAffected).IsEqualTo(1);
    await Assert.That(await _survivesAsync(LEADER_TABLE, overflow)).IsFalse();
    await Assert.That(await _survivesAsync(LEADER_TABLE, newest)).IsTrue();
    await Assert.That(await _scalarAsync<string?>(
      "SELECT setting_key FROM wh_settings WHERE setting_key = @k", new { k = ROW_CAP_KEY })).IsEqualTo(ROW_CAP_KEY);
  }

  [Test]
  public async Task TheAdoptionGate_CountsTheBacklog_AndAcknowledgingOpensItAsync() {
    var expired = Guid.CreateVersion7();
    await _seedAsync(LEADER_TABLE, expired, idleHours: 48);
    var coordinator = _coordinator();
    await coordinator.SyncPerspectiveRetentionAsync([_declaration(_leaderClr, ttlSeconds: 3600)]);

    await Assert.That(await coordinator.CountPerspectiveRetentionBacklogAsync(_leaderClr)).IsEqualTo(1L);
    var gated = await coordinator.ReapEnrolledPerspectiveRowsAsync();
    await Assert.That(gated.Status).IsNotEqualTo("unsupported");
    await Assert.That(await _survivesAsync(LEADER_TABLE, expired)).IsTrue()
      .Because("an unacknowledged perspective reports its backlog and removes nothing");

    await coordinator.AcknowledgeRetentionEnforcementAsync(_leaderClr);
    var swept = await coordinator.ReapEnrolledPerspectiveRowsAsync(batchSize: 100);
    await Assert.That(swept.RowsAffected).IsEqualTo(1);
    await Assert.That(await coordinator.AdoptEnrolledPerspectiveRetentionAsync()).IsEmpty()
      .Because("an acknowledged perspective is never adopted twice");
  }

  [Test]
  public async Task TheSeamAndCascadeMembers_ReadAndWriteTheSharedTablesAsync() {
    var held = Guid.CreateVersion7();
    var free = Guid.CreateVersion7();
    var orphan = Guid.CreateVersion7();
    var outOfScope = Guid.CreateVersion7();
    await _seedAsync(FOLLOWER_TABLE, held, idleHours: 0, user: "user-1");
    await _seedAsync(FOLLOWER_TABLE, free, idleHours: 0, user: null);
    await _seedAsync(FOLLOWER_TABLE, orphan, idleHours: 0);
    await _seedAsync(FOLLOWER_TABLE, outOfScope, idleHours: 0);
    await _seedAsync(LEADER_TABLE, free, idleHours: 0);
    var coordinator = _coordinator();

    var names = await coordinator.GetPerspectiveTableNamesAsync([_leaderClr, _followerClr]);
    await Assert.That(names).Contains(new PerspectiveTableName(_followerClr, FOLLOWER_TABLE));
    var rows = await coordinator.GetPerspectiveRowsByIdsAsync(_followerClr, FOLLOWER_TABLE, [held]);
    await Assert.That(rows.Single().Reason).IsEqualTo("cascade");
    await Assert.That(rows.Single().Scope!.Value.GetProperty("u").GetString()).IsEqualTo("user-1");
    await Assert.That((await coordinator.GetPerspectiveRowsByIdsAsync(_followerClr, FOLLOWER_TABLE, [free])).Single().Scope).IsNull();

    await coordinator.HoldPerspectiveRowDestructionAsync([new PerspectiveRowRef(FOLLOWER_TABLE, held)], DateTimeOffset.UtcNow.AddHours(1));
    await Assert.That(await coordinator.ReconcileFollowerPresenceAsync(FOLLOWER_TABLE, [LEADER_TABLE], [orphan, held])).IsEqualTo(1)
      .Because("scoped to the rebuilt streams, only the unheld one absent from every announcer goes");
    await Assert.That(await _survivesAsync(FOLLOWER_TABLE, orphan)).IsFalse();
    await Assert.That(await _survivesAsync(FOLLOWER_TABLE, outOfScope)).IsTrue()
      .Because("a row outside the rebuilt streams is not read by a scoped reconcile");
    await Assert.That(await coordinator.ReconcileFollowerPresenceAsync(FOLLOWER_TABLE, [LEADER_TABLE])).IsEqualTo(1)
      .Because("unscoped, the whole table is reconciled; the held row still survives");
    await Assert.That(await _survivesAsync(FOLLOWER_TABLE, outOfScope)).IsFalse();
    await Assert.That(await coordinator.CascadeDeletePerspectiveRowsAsync(FOLLOWER_TABLE, [held, free])).IsEqualTo(1);
    await Assert.That(await _survivesAsync(FOLLOWER_TABLE, held)).IsTrue();

    await coordinator.ReleasePerspectiveRowHoldsAsync([new PerspectiveRowRef(FOLLOWER_TABLE, held)]);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_perspective_row_hold", null)).IsEqualTo(0L);
    var failures = await coordinator.RecordPerspectiveRowDestructionFailureAsync(
      [new PerspectiveRowRef(FOLLOWER_TABLE, held)], TimeSpan.FromMinutes(1), maxRetries: 3, OnDestroyFailure.RetryThenKeep);
    await Assert.That(failures).IsEqualTo(1);

    await coordinator.RequeueRowEvictionsAsync([new PerspectiveRowRef(LEADER_TABLE, held), new PerspectiveRowRef(LEADER_TABLE, held)]);
    await Assert.That(await coordinator.DrainRowEvictionJournalAsync()).IsEquivalentTo([new PerspectiveRowRef(LEADER_TABLE, held)]);
    await Assert.That(await coordinator.DrainRowEvictionJournalAsync(limit: 10)).IsEmpty()
      .Because("the drain is a DELETE ... RETURNING claim, so a second drain gets nothing");
  }

  [Test]
  public async Task ADeclarationWithNoRegistryRow_IsReportedAsync() {
    await _coordinator().SyncPerspectiveRetentionAsync([_declaration("Unknown.Model", ttlSeconds: 60)]);

    await Assert.That(_log.Warnings.Single()).Contains("Unknown.Model")
      .Because("a declaration matching no registry row is a key drift that would leave the perspective silently un-enrolled");

    var unlogged = new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    await unlogged.SyncPerspectiveRetentionAsync([_declaration("Unknown.Model", ttlSeconds: 60), _declaration(_leaderClr, ttlSeconds: 60)]);
    await Assert.That(await _scalarAsync<int?>(
      "SELECT row_ttl_seconds FROM wh_perspective_registry WHERE clr_type_name = @c", new { c = _leaderClr })).IsEqualTo(60)
      .Because("with no logger the drift is unreported, and the declarations after it are still carried");
  }

  [Test]
  public async Task ASweepFunctionThatReturnsNoRow_IsReportedAsNoResultAsync() {
    // Both sweep functions always return one row; one that returned none must not throw or read a stale reader.
    await _executeAsync(@"
      CREATE OR REPLACE FUNCTION reap_enrolled_perspective_rows(p_batch_size INTEGER DEFAULT 5000)
      RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT)
      AS $$ SELECT NULL::TEXT, 0, 0::DOUBLE PRECISION, NULL::TEXT WHERE FALSE $$ LANGUAGE sql;
      CREATE OR REPLACE FUNCTION reap_perspective_row_caps(p_batch_size INTEGER DEFAULT 5000)
      RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT)
      AS $$ SELECT NULL::TEXT, 0, 0::DOUBLE PRECISION, NULL::TEXT WHERE FALSE $$ LANGUAGE sql;", new { });
    var coordinator = _coordinator();

    await Assert.That(await coordinator.ReapEnrolledPerspectiveRowsAsync()).IsEqualTo(new PerspectiveRowReapResult(0, "no result"));
    await Assert.That(await coordinator.ReapPerspectiveRowCapsAsync()).IsEqualTo(new PerspectiveRowReapResult(0, "no result"));
  }

  [Test]
  public async Task EmptyInputs_ReturnWithoutTouchingTheDatabaseAsync() {
    // A connection string with nowhere to connect: any of these that opened a connection would throw.
    var coordinator = new DapperWorkCoordinator(
      "Host=127.0.0.1;Port=1;Timeout=1", Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<DapperWorkCoordinator>.Instance, schema: "public");

    await coordinator.SyncPerspectiveRetentionAsync([]);
    await Assert.That(await coordinator.GetPerspectiveRowsAboutToReapAsync([])).IsEmpty();
    await coordinator.HoldPerspectiveRowDestructionAsync([], DateTimeOffset.UtcNow);
    await coordinator.ReleasePerspectiveRowHoldsAsync([]);
    await Assert.That(await coordinator.RecordPerspectiveRowDestructionFailureAsync([], TimeSpan.Zero, 1, OnDestroyFailure.RetryThenKeep)).IsEqualTo(0);
    await coordinator.RequeueRowEvictionsAsync([]);
    await Assert.That(await coordinator.GetPerspectiveTableNamesAsync([])).IsEmpty();
    await Assert.That(await coordinator.GetPerspectiveRowsByIdsAsync(_leaderClr, LEADER_TABLE, [])).IsEmpty();
    await Assert.That(await coordinator.CascadeDeletePerspectiveRowsAsync(LEADER_TABLE, [])).IsEqualTo(0);
    await Assert.That(await coordinator.ReconcileFollowerPresenceAsync(FOLLOWER_TABLE, [])).IsEqualTo(0);
    await Assert.That(await coordinator.ReconcileFollowerPresenceAsync(FOLLOWER_TABLE, [], [Guid.CreateVersion7()])).IsEqualTo(0);
    await Assert.That(await coordinator.ReconcileFollowerPresenceAsync(FOLLOWER_TABLE, [LEADER_TABLE], [])).IsEqualTo(0);
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    var coordinator = _coordinator();

    await Assert.That(async () => await coordinator.SyncPerspectiveRetentionAsync(null!)).Throws<ArgumentNullException>();
  }

  private static PerspectiveRetentionDeclaration _declaration(string clr, int? ttlSeconds, int? cap = null) =>
    new(clr, Enrolled: true, TtlSeconds: ttlSeconds, MaxAgeSeconds: null, CapPerScope: cap, CapScopeKey: cap is null ? null : "u");

  private MaintenanceWorker _worker(IPerspectiveRowDestructionGuard? guard = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => _coordinator());
    if (guard is not null) {
      services.AddSingleton(guard);
    }
    var provider = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      provider.GetRequiredService<IServiceScopeFactory>(), gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  private async Task _seedAsync(string table, Guid id, int idleHours, string? user = "user-0") =>
    // Business time is seeded on the database clock, which is the clock the sweep compares against. A null user seeds
    // a row with no scope at all.
    await _executeAsync($@"
      INSERT INTO {table} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, CASE WHEN @user::text IS NULL THEN NULL ELSE jsonb_build_object('u', @user::text) END,
              NOW() - make_interval(hours => @h), NOW() - make_interval(hours => @h), 1)",
      new { id, user, h = idleHours });

  private async Task<bool> _survivesAsync(string table, Guid id) =>
    await _scalarAsync<long>($"SELECT count(*) FROM {table} WHERE id = @id", new { id }) > 0;

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private async Task _executeAsync(string sql, object parameters) {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object? parameters) {
    await using var connection = await _openAsync();
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }

  /// <summary>A guard over the leader that defers until its resource is clean.</summary>
  private sealed class LeaderGuard : IPerspectiveRowDestructionGuard {
    public bool Clean { get; set; }
    public List<Guid> Offered { get; } = [];
    public List<Guid> Released { get; } = [];
    public Dictionary<Guid, System.Text.Json.JsonElement?> Scopes { get; } = [];
    public IReadOnlyCollection<Type> GuardedModels => [typeof(DapperLeaderModel)];

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken cancellationToken = default) {
      Offered.AddRange(targets.Select(t => t.RowId));
      foreach (var target in targets) {
        Scopes[target.RowId] = target.Scope;
      }
      return ValueTask.FromResult<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(
        targets.ToDictionary(t => t.RowId, _ => Clean
          ? PerspectiveRowDecision.Proceed()
          : PerspectiveRowDecision.Defer(DateTimeOffset.UtcNow.AddHours(1))));
    }

    public ValueTask OnAfterReapAsync(IReadOnlyList<PerspectiveRowDestructionTarget> released, CancellationToken cancellationToken = default) {
      Released.AddRange(released.Select(t => t.RowId));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Collects the coordinator's warnings.</summary>
  private sealed class WarningLog : ILogger<DapperWorkCoordinator> {
    public ConcurrentQueue<string> Warnings { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (IsEnabled(logLevel)) {
        Warnings.Enqueue(formatter(state, exception));
      }
    }
  }
}
