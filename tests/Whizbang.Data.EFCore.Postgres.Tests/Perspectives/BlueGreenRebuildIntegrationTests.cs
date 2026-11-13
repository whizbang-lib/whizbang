// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The blue-green rebuild end to end on the EF Core driver (#1025): the rebuilt rows go to a shadow table while
/// readers keep the complete live table, a write committed during the rebuild is caught up, and the swap leaves the
/// rebuilt rows under the live name with the previous table kept.
/// </summary>
/// <remarks>
/// The live table is seeded with rows an older version of the perspective wrote (a stale balance), as a consumer
/// re-projecting to fill a new column has. A reader of the live table during the rebuild must see exactly those rows,
/// every time: before #1025 the rebuild replayed into the live table, so a reader saw it change stream by stream.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveTableSwapperFactory.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class BlueGreenRebuildIntegrationTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalancePerspective";
  // The registry is keyed by the MODEL, which is what the schema pass writes. Seeding it under the
  // PERSPECTIVE name built a world where the old lookup worked, which is how #1308 kept green
  // integration coverage over a mode that could not run against a real schema.
  private const string MODEL = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalanceModel";
  private const string TABLE = "wh_per_rebuild_balance";
  private const decimal STALE = 999m;

  [Test]
  public async Task BlueGreen_ReadersSeeTheCompleteLiveTableThroughout_AndTheSwapInstallsTheRebuiltRowsAsync() {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var snapshots = new List<Dictionary<Guid, decimal>>();
    await using var sp = _services(async () => snapshots.Add(await _liveBalancesAsync()),
      onFirstRun: async root => await _appendAsync(root, new RebuildCreditedEvent { StreamId = streams[0], Amount = 7m }));
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[0], Amount = 100m });
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[0], Amount = 50m });
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[1], Amount = 100m });
    await _appendAsync(sp, new RebuildDebitedEvent { StreamId = streams[1], Amount = 75m });
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[2], Amount = 500m });
    await _seedStaleLiveRowsAsync(streams);
    await _registerTableAsync();
    var stale = await _liveBalancesAsync();

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(snapshots).IsNotEmpty();
    foreach (var snapshot in snapshots) {
      await Assert.That(snapshot).IsEquivalentTo(stale)
        .Because("Every read of the live table during the rebuild saw the whole previous projection, never a half-rebuilt one.");
    }
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> {
      [streams[0]] = 157m,
      [streams[1]] = 25m,
      [streams[2]] = 500m,
    }).Because("The rebuilt rows are live after the swap, including the credit committed while the rebuild ran.");
    await Assert.That(await _balancesAsync(TABLE + "_bg_old")).IsEquivalentTo(stale)
      .Because("The previous table is kept by default as the way back.");
    await Assert.That(await sp.GetRequiredService<IPerspectiveRebuilder>().GetRebuildStatusAsync(PERSPECTIVE)).IsNull();
  }

  /// <summary>
  /// #2299: a blue-green rebuild keeps the live table's storage settings. The swap installs the shadow table under the
  /// live name, and <c>LIKE … INCLUDING ALL</c> copies no storage parameters, so before #2299 the rebuilt table came out
  /// with none: its fillfactor and autovacuum settings, and its TOAST relation's, were lost until the next start.
  /// </summary>
  [Test]
  public async Task BlueGreen_TheSwappedTable_KeepsTheLiveTablesStorageSettingsAsync() {
    var stream = Guid.NewGuid();
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask);
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 10m });
    await _registerTableAsync();
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync($"""
        ALTER TABLE {TABLE} SET (fillfactor = 90, autovacuum_vacuum_threshold = 500, toast.autovacuum_vacuum_scale_factor = 0.05)
        """);
    }

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> { [stream] = 10m });
    await using var verify = await _openAsync();
    await using var cmd = verify.CreateCommand();
    cmd.CommandText = $"""
      SELECT array_to_string(c.reloptions, ','), array_to_string(t.reloptions, ',')
      FROM pg_class c LEFT JOIN pg_class t ON t.oid = c.reltoastrelid WHERE c.oid = '{TABLE}'::regclass
      """;
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    await Assert.That(reader.GetString(0)).IsEqualTo("fillfactor=90,autovacuum_vacuum_threshold=500")
      .Because("the rebuilt table is the one now live, and it holds the settings the live table had.");
    await Assert.That(reader.GetString(1)).IsEqualTo("autovacuum_vacuum_scale_factor=0.05");
  }

  [Test]
  public async Task Store_InARedirectedFlow_ReadsWritesAndPurgesTheShadowTableOnlyAsync() {
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync($"CREATE TABLE {TABLE}_bg (LIKE {TABLE} INCLUDING ALL)");
    }
    var id = Guid.NewGuid();
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(context, TABLE);

    using (PerspectiveTableRedirect.Begin(TABLE, TABLE + "_bg")) {
      await store.UpsertAsync(id, new RebuildBalanceModel { Id = id, Balance = 5m });
      await Assert.That((await store.GetByStreamIdAsync(id))!.Balance).IsEqualTo(5m);
      await Assert.That(await store.GetMetadataByStreamIdAsync(id)).IsNotNull();
      await Assert.That((await store.ReadForApplyAsync(id)).Version.State).IsEqualTo(PerspectiveRowVersionState.Present);
    }
    await Assert.That(await store.GetByStreamIdAsync(id)).IsNull().Because("The live table never saw the redirected write.");

    using (PerspectiveTableRedirect.Begin(TABLE, TABLE + "_bg")) {
      await store.PurgeAsync(id);
      await store.PurgeByPartitionKeyAsync(id);
      await Assert.That(await store.GetByStreamIdAsync(id)).IsNull();
    }
    using (PerspectiveTableRedirect.Begin("wh_per_some_other_table", "elsewhere")) {
      await Assert.That(await store.GetByStreamIdAsync(id)).IsNull()
        .Because("A redirect of another table leaves this store on its own table.");
    }
  }

  [Test]
  public async Task Upsert_ThatCannotTakeTheAtomicPath_InARedirectedFlow_RefusesRatherThanWriteTheLiveTableAsync() {
    await using var context = CreateDbContext();
    var strategy = new EntityFrameworkOnlyUpsertStrategy();
    var id = Guid.NewGuid();

    using (PerspectiveTableRedirect.Begin(TABLE, TABLE + "_bg")) {
      await Assert.That(() => strategy.UpsertPerspectiveRowAsync(
          context, TABLE, id, new RebuildBalanceModel { Id = id, Balance = 1m },
          new PerspectiveMetadata { EventType = "E", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
          new PerspectiveScope()))
        .ThrowsExactly<InvalidOperationException>().WithMessageContaining("blue-green");
    }
    await Assert.That(await _balancesAsync(TABLE)).IsEmpty();
  }

  [Test]
  public async Task SwapperFactory_WithAPostgresContext_BuildsASwapper_AndWithoutOne_NoneAsync() {
    var services = new ServiceCollection();
    services.AddScoped(_ => new WorkCoordinationDbContext(DbContextOptions));
    await using var sp = services.BuildServiceProvider();
    var inMemory = new ServiceCollection();
    inMemory.AddDbContext<InMemoryContext>(o => o.UseInMemoryDatabase("swapper-factory"));
    await using var inMemorySp = inMemory.BuildServiceProvider();

    var swapper = EFCorePerspectiveTableSwapperFactory.Create(sp, typeof(WorkCoordinationDbContext));
    await _registerTableAsync();

    await Assert.That(swapper).IsNotNull();
    await Assert.That(await swapper!.FindTableAsync(MODEL, CancellationToken.None)).IsEqualTo(TABLE);
    await Assert.That(await swapper.FindTableAsync(PERSPECTIVE, CancellationToken.None)).IsNull()
      .Because("the registry holds models, so a perspective name finds nothing: asking with it is #1308");
    await Assert.That(EFCorePerspectiveTableSwapperFactory.Create(inMemorySp, typeof(InMemoryContext))).IsNull()
      .Because("With no connection of its own to open, there is no swapper, and a blue-green rebuild replays in place.");
  }

  // ── #2301: a perspective table rewritten by a blue-green rebuild ───────────────────────────────

  /// <summary>
  /// #2301: on a server where neither pg_squeeze nor pg_repack is usable, a perspective table whose fillfactor was
  /// lowered is rewritten under <c>Online</c> by a blue-green rebuild of its perspective: the rebuilt table holds the
  /// declared fillfactor (the shadow took the live table's settings, #2299), its rows are the replayed ones, an update
  /// of one of them is HOT, the request is cleared and the history says the rebuild carried it out. <c>Auto</c> first
  /// rewrites nothing: the rebuild is opted into.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task Online_WithNoExtension_RebuildsALoweredFillFactorTable_AtTheNewFillFactorAsync(CancellationToken cancellationToken) {
    var streams = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToArray();
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask);
    foreach (var stream in streams) {
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 10m });
    }
    await _seedStaleLiveRowsAsync(streams);
    await _registerTableAsync();
    await _execAsync($"ANALYZE {TABLE}");
    await _lowerFillFactorAsync();
    var before = await _scalarAsync<long>($"SELECT relfilenode::bigint FROM pg_class WHERE oid = '{TABLE}'::regclass");
    await Assert.That(await _isHotUpdateAsync(streams[0])).IsFalse().Because("the control: the stale rows sit on full pages");

    await _maintenanceCycleAsync(sp, TableRewriteStrategy.Auto);
    await Assert.That(await _scalarAsync<long>($"SELECT relfilenode::bigint FROM pg_class WHERE oid = '{TABLE}'::regclass")).IsEqualTo(before)
      .Because("Auto never starts an online rewrite, a rebuild included");

    await _maintenanceCycleAsync(sp, TableRewriteStrategy.Online);

    await Assert.That(await _scalarAsync<string>($"SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = '{TABLE}'::regclass"))
      .Contains("fillfactor=90");
    await Assert.That((await _liveBalancesAsync()).Values.Distinct()).IsEquivalentTo([10m]).Because("the rows are the replayed ones");
    await Assert.That(await _isHotUpdateAsync(streams[0])).IsTrue()
      .Because("the rebuild filled every page to fillfactor 90, so an update stays on its page");
    await Assert.That(await _scalarAsync<string>("SELECT setting_value FROM wh_settings WHERE setting_key = 'pending_table_rewrites'"))
      .DoesNotContain(TABLE);
    await Assert.That(await _scalarAsync<string>($"SELECT strategy || '/' || reason FROM wh_table_rewrite_history WHERE table_name = '{TABLE}'"))
      .IsEqualTo("BlueGreenRebuild/FillFactorDecrease");
  }

  /// <summary>
  /// #2301: two blue-green rebuilds of one table at once: the second, started while the first is replaying, fails and
  /// says why without touching the shadow, and the first completes. Without the guard the second would drop the first
  /// one's shadow mid-replay.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task TwoRebuildsOfOneTable_TheSecondFails_AndTheFirstCompletesAsync(CancellationToken cancellationToken) {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    RebuildResult? second = null;
    var started = false;
    // Once: the second rebuild's own runner would otherwise start a third from the same hook.
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: async root => {
      if (!started) {
        started = true;
        second = await root.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE, CancellationToken.None);
      }
    });
    foreach (var stream in streams) {
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 5m });
    }
    await _registerTableAsync();

    var first = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE, cancellationToken);

    await Assert.That(second).IsNotNull();
    await Assert.That(second!.Success).IsFalse();
    await Assert.That(second.Error).Contains("another blue-green rebuild of " + TABLE + " is in flight");
    await Assert.That(first.Success).IsTrue().Because(first.Error ?? "");
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> { [streams[0]] = 5m, [streams[1]] = 5m });
  }

  /// <summary>
  /// #2301: a table another blue-green rebuild holds, on any instance, is not rebuilt by the maintenance cycle; its
  /// request stays queued for a later cycle.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task Online_SkipsATableAnotherRebuildHolds_AndLeavesItQueuedAsync(CancellationToken cancellationToken) {
    var stream = Guid.NewGuid();
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask);
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 5m });
    await _seedStaleLiveRowsAsync([stream]);
    await _registerTableAsync();
    await _lowerFillFactorAsync();
    var before = await _scalarAsync<long>($"SELECT relfilenode::bigint FROM pg_class WHERE oid = '{TABLE}'::regclass");
    await using var held = await sp.GetRequiredService<IPerspectiveTableSwapper>().TryLockRebuildAsync(TABLE, cancellationToken);
    await Assert.That(held).IsNotNull();

    await _maintenanceCycleAsync(sp, TableRewriteStrategy.Online);

    await Assert.That(await _scalarAsync<long>($"SELECT relfilenode::bigint FROM pg_class WHERE oid = '{TABLE}'::regclass")).IsEqualTo(before);
    await Assert.That(await _scalarAsync<string>("SELECT setting_value FROM wh_settings WHERE setting_key = 'pending_table_rewrites'"))
      .Contains(TABLE);
  }

  /// <summary>#2301: a framework table cannot be rebuilt from events, so with no extension it waits; nothing rewrites it.</summary>
  [Test]
  [Timeout(300000)]
  public async Task Online_WithNoExtension_AFrameworkTableWaitsAsync(CancellationToken cancellationToken) {
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask);
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    await _execAsync("SELECT wh_request_table_rewrite('wh_settings')");
    var before = await _scalarAsync<long>("SELECT relfilenode::bigint FROM pg_class WHERE oid = 'wh_settings'::regclass");

    await _maintenanceCycleAsync(sp, TableRewriteStrategy.Online);

    await Assert.That(await _scalarAsync<long>("SELECT relfilenode::bigint FROM pg_class WHERE oid = 'wh_settings'::regclass")).IsEqualTo(before);
    await Assert.That(await _scalarAsync<string>("SELECT setting_value FROM wh_settings WHERE setting_key = 'pending_table_rewrites'"))
      .Contains("wh_settings");
  }

  /// <summary>One maintenance cycle's online rewrite, under <paramref name="strategy"/>, on a server with neither extension usable.</summary>
  private static async Task _maintenanceCycleAsync(ServiceProvider sp, TableRewriteStrategy strategy) {
    var options = Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = strategy });
    var registry = new CapabilityRegistry([new NotPermittedExtensions()], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    var step = new OnlineTableRewriteStep(
      [new PgSqueezeTableRewriter(), new BlueGreenTableRewriter(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<BlueGreenTableRewriter>.Instance)],
      options, registry, NullLogger<OnlineTableRewriteStep>.Instance);
    await using var scope = sp.CreateAsyncScope();
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>(), JsonContextRegistry.CreateCombinedOptions())).BuildServiceProvider();
    await step.RunAsync(services, CancellationToken.None);
  }

  /// <summary>pg_squeeze and pg_repack, each probed NotPermitted, as on a server that does not allowlist them.</summary>
  private sealed class NotPermittedExtensions : ICapabilityProvider {
    public string Name => RequiresPostgresExtensionAttribute.PROVIDER;
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = [_describe("pg_squeeze"), _describe("pg_repack")];
    public IReadOnlyList<string> Scopes { get; } = ["db"];

    public Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<CapabilityStatus>>([.. Descriptors.Select(d => new CapabilityStatus(d, scope, CapabilityState.NotPermitted, null, null, null))]);

    private static CapabilityDescriptor _describe(string name) =>
      new(new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, name), "online rewrite", ["Online table rewrite"], true, [],
        new Dictionary<CapabilityState, string>());
  }

  /// <summary>Lowers the table's fillfactor through the schema pass's reconcile, as a start that finds 90 declared does.</summary>
  private async Task _lowerFillFactorAsync() {
    await using var db = await _openAsync();
    await PerspectiveStorageParameters.ReconcileAsync(
      db, "public", [PerspectiveStorageParameters.MARKER + TABLE + " fillfactor=90"], null, CancellationToken.None);
  }

  /// <summary>Whether an update of the row (to its version, which no index covers) is HOT; read in its own transaction, rolled back.</summary>
  private async Task<bool> _isHotUpdateAsync(Guid id) {
    await using var db = await _openAsync();
    await using var tx = await db.BeginTransactionAsync();
    await using (var update = new NpgsqlCommand($"UPDATE {TABLE} SET version = version + 1 WHERE id = @id", db, tx)) {
      update.Parameters.AddWithValue(nameof(id), id);
      await update.ExecuteNonQueryAsync();
    }
    await using var hot = new NpgsqlCommand($"SELECT pg_stat_get_xact_tuples_hot_updated('{TABLE}'::regclass)", db, tx);
    var count = (long)(await hot.ExecuteScalarAsync())!;
    await tx.RollbackAsync();
    return count == 1;
  }

  private async Task _execAsync(string sql) {
    await using var db = await _openAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var db = await _openAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private sealed class InMemoryContext(DbContextOptions<InMemoryContext> options) : DbContext(options);

  private sealed class EntityFrameworkOnlyUpsertStrategy : BaseUpsertStrategy {
    protected override bool UsesAtomicUpsert => false;
  }

  // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

  private ServiceProvider _services(Func<Task> beforeEachStream, Func<IServiceProvider, Task> onFirstRun) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(s => s.GetRequiredService<WorkCoordinationDbContext>());
    services.AddScoped<IEventStore>(s => new EFCoreEventStore<WorkCoordinationDbContext>(s.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IEventStoreQuery>(s => new EFCoreFilterableEventStoreQuery(s.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IPerspectiveStore<RebuildBalanceModel>>(s =>
      new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(s.GetRequiredService<WorkCoordinationDbContext>(), TABLE));
    services.AddScoped<RebuildBalancePerspective>();
    var asm = typeof(BlueGreenRebuildIntegrationTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildBalancePerspectiveRunner"));
    var registryType = asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" &&
      t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated");
    services.AddSingleton(registryType);
    services.AddSingleton<IPerspectiveRunnerRegistry>(s =>
      new ObservingRegistry((IPerspectiveRunnerRegistry)s.GetRequiredService(registryType), beforeEachStream,
        () => onFirstRun(s)));
    services.AddScoped<IPerspectiveCheckpointCompleter>(s =>
      new EFCorePostgresPerspectiveCheckpointCompleter(s.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddSingleton<IPerspectiveTableSwapper>(s =>
      EFCorePerspectiveTableSwapperFactory.Create(s, typeof(WorkCoordinationDbContext))!);
    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();
    return services.BuildServiceProvider();
  }

  /// <summary>Wraps the generated registry so each stream's replay first takes a reader's view of the live table.</summary>
  private sealed class ObservingRegistry(IPerspectiveRunnerRegistry inner, Func<Task> beforeEachStream, Func<Task> onFirstRun)
      : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      inner.GetRunner(perspectiveName, serviceProvider) is { } runner ? new ObservingRunner(runner, beforeEachStream, onFirstRun) : null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => inner.GetRegisteredPerspectives();
    public IReadOnlyList<Type> GetEventTypes() => inner.GetEventTypes();
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => inner.LifecycleStagesWithReceptors;
  }

  private sealed class ObservingRunner(IPerspectiveRunner inner, Func<Task> beforeEachStream, Func<Task> onFirstRun) : IPerspectiveRunner {
    private bool _ran;
    public Type PerspectiveType => inner.PerspectiveType;

    public async Task<IReadOnlyList<PerspectiveCursorCompletion>> RunRebuildAsync(Guid physicalStreamId, string perspectiveName, CancellationToken cancellationToken = default) {
      await beforeEachStream();
      var completions = await inner.RunRebuildAsync(physicalStreamId, perspectiveName, cancellationToken);
      if (!_ran) {
        _ran = true;
        await onFirstRun();
      }
      return completions;
    }

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      inner.RunAsync(streamId, perspectiveName, lastProcessedEventId, cancellationToken);
    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) =>
      inner.RunWithEventsAsync(streamId, perspectiveName, lastProcessedEventId, events, cancellationToken);
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      inner.RewindAndRunAsync(streamId, perspectiveName, triggeringEventId, cancellationToken);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      inner.BootstrapSnapshotAsync(streamId, perspectiveName, lastProcessedEventId, cancellationToken);
  }

  private static async Task _appendAsync<TEvent>(IServiceProvider root, TEvent payload) where TEvent : IEvent {
    await using var scope = root.CreateAsyncScope();
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var streamId = payload switch {
      RebuildCreditedEvent c => c.StreamId,
      RebuildDebitedEvent d => d.StreamId,
      _ => throw new ArgumentException("Unexpected event", nameof(payload)),
    };
    await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(streamId, envelope);
  }

  private async Task _seedStaleLiveRowsAsync(Guid[] streams) {
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(context, TABLE);
    foreach (var stream in streams) {
      await store.UpsertAsync(stream, new RebuildBalanceModel { Id = stream, Balance = STALE });
    }
  }

  private async Task _registerTableAsync() {
    await using var conn = await _openAsync();
    await conn.ExecuteAsync("""
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name, perspective_names)
      VALUES (@name, @table, '{}'::jsonb, 'h', 'svc', ARRAY[@perspective]) ON CONFLICT DO NOTHING
      """, new { name = MODEL, table = TABLE, perspective = PERSPECTIVE });
  }

  private Task<Dictionary<Guid, decimal>> _liveBalancesAsync() => _balancesAsync(TABLE);

  private async Task<Dictionary<Guid, decimal>> _balancesAsync(string table) {
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT id, (data->>'Balance')::numeric FROM {table}";
    var balances = new Dictionary<Guid, decimal>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      balances[reader.GetGuid(0)] = reader.GetDecimal(1);
    }
    return balances;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }
}
