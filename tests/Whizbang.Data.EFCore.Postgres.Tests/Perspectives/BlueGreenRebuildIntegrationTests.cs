using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Whizbang.Core.ValueObjects;

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
    await Assert.That(await swapper!.FindTableAsync(PERSPECTIVE, CancellationToken.None)).IsEqualTo(TABLE);
    await Assert.That(EFCorePerspectiveTableSwapperFactory.Create(inMemorySp, typeof(InMemoryContext))).IsNull()
      .Because("With no connection of its own to open, there is no swapper, and a blue-green rebuild replays in place.");
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
    await using (var scope = root.CreateAsyncScope()) {
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
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@name, @table, '{}'::jsonb, 'h', 'svc') ON CONFLICT DO NOTHING
      """, new { name = PERSPECTIVE, table = TABLE });
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
