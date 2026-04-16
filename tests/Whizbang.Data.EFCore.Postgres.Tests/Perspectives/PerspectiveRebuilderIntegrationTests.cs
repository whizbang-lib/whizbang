using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// End-to-end integration tests for PerspectiveRebuilder against a real Postgres event store
/// and real EFCore perspective storage. Covers RebuildInPlaceAsync and RebuildStreamsAsync.
/// Paired with the unit tests in Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderTests.cs.
/// </summary>
[Category("Integration")]
public class PerspectiveRebuilderIntegrationTests : EFCoreTestBase {

  private const string RebuildBalancePerspectiveName =
      "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalancePerspective";

  private const string RebuildInventoryPerspectiveName =
      "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildInventoryPerspective";

  /// <summary>
  /// Builds a ServiceProvider wired for rebuild: real Postgres-backed event store,
  /// real EFCorePostgresPerspectiveStore, the source-generated runner registry, and
  /// the registered RebuildBalancePerspective runner. Uses reflection to pick up the
  /// generated types so the test survives compile passes where generators don't run.
  /// </summary>
  private ServiceProvider _buildRebuildServices() {
    var services = new ServiceCollection();
    services.AddLogging();

    // DbContext scoped — one per DI scope, same options as EFCoreTestBase uses for direct access
    services.AddScoped<WorkCoordinationDbContext>(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<WorkCoordinationDbContext>());

    // Real Postgres event store — AppendAsync writes to wh_event_store
    services.AddScoped<IEventStore>(sp =>
        new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>()));

    // Real Postgres IEventStoreQuery — reads from wh_event_store via IQueryable
    services.AddScoped<IEventStoreQuery>(sp =>
        new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));

    // Real EFCorePostgresPerspectiveStore<RebuildBalanceModel>
    services.AddScoped<IPerspectiveStore<RebuildBalanceModel>>(sp =>
        new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(
            sp.GetRequiredService<WorkCoordinationDbContext>(),
            "rebuild_balance"));

    services.AddScoped<IPerspectiveStore<RebuildInventoryModel>>(sp =>
        new EFCorePostgresPerspectiveStore<RebuildInventoryModel>(
            sp.GetRequiredService<WorkCoordinationDbContext>(),
            "rebuild_inventory"));

    // Register the perspectives and their source-generated runners. Use reflection to locate
    // the generated runner types so this file compiles even when the generator hasn't produced
    // output yet (e.g., during dotnet format passes). Same pattern as PerspectiveRunnerModelActionTests.
    services.AddScoped<RebuildBalancePerspective>();
    services.AddScoped<RebuildInventoryPerspective>();

    var asm = typeof(PerspectiveRebuilderIntegrationTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildBalancePerspectiveRunner"));
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildInventoryPerspectiveRunner"));

    // Source-generated IPerspectiveRunnerRegistry lives in the .Generated namespace.
    var registryType = asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" &&
        t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated");
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry), registryType);

    return services.BuildServiceProvider();
  }

  private static async Task _appendEventAsync<TEvent>(IEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
  }

  [Test]
  public async Task RebuildInPlaceAsync_ReplaysAllStreamsAndUpdatesProjectionAndCursorsAsync() {
    // Arrange: seed three streams with a mix of Credited/Debited events.
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var expectedBalances = new Dictionary<Guid, decimal> {
      [streams[0]] = 150m,  // +100 + 50
      [streams[1]] = 25m,   // +100 - 75
      [streams[2]] = 500m   // +500
    };

    await using var sp = _buildRebuildServices();

    // Use a scoped IEventStore to append — matches the production scope lifetime.
    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();

      await _appendEventAsync(eventStore, streams[0], new RebuildCreditedEvent { StreamId = streams[0], Amount = 100m });
      await _appendEventAsync(eventStore, streams[0], new RebuildCreditedEvent { StreamId = streams[0], Amount = 50m });

      await _appendEventAsync(eventStore, streams[1], new RebuildCreditedEvent { StreamId = streams[1], Amount = 100m });
      await _appendEventAsync(eventStore, streams[1], new RebuildDebitedEvent { StreamId = streams[1], Amount = 75m });

      await _appendEventAsync(eventStore, streams[2], new RebuildCreditedEvent { StreamId = streams[2], Amount = 500m });
    }

    // Act: rebuild in place.
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);

    // Assert: rebuild result reports success across all three streams.
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.PerspectiveName).IsEqualTo(RebuildBalancePerspectiveName);
    await Assert.That(result.StreamsProcessed).IsEqualTo(3);
    await Assert.That(result.Error).IsNull();

    // Assert: the projection table reflects the replayed balances.
    await using var verifyContext = CreateDbContext();
    foreach (var (streamId, expected) in expectedBalances) {
      var row = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
          .AsNoTracking()
          .FirstOrDefaultAsync(r => r.Id == streamId);
      await Assert.That(row).IsNotNull();
      await Assert.That(row!.Data.Balance).IsEqualTo(expected);
    }
  }

  [Test]
  public async Task RebuildStreamsAsync_WithSubset_UpdatesOnlyTargetedStreamsAsync() {
    // Arrange: seed five streams with events. Do NOT project ahead of time — this keeps the
    // test focused on per-stream isolation without tripping over the rebuild's replay-on-top
    // semantics (replay loads existing projection state, so re-replaying after a baseline
    // double-applies additive events like Credited).
    var streams = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      foreach (var streamId in streams) {
        await _appendEventAsync(eventStore, streamId, new RebuildCreditedEvent { StreamId = streamId, Amount = 100m });
      }
    }

    // Act: rebuild only two of the five streams.
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);
    var targeted = new[] { streams[0], streams[2] };
    var result = await rebuilder.RebuildStreamsAsync(RebuildBalancePerspectiveName, targeted, CancellationToken.None);

    // Assert: result reports exactly the two targeted streams.
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);

    // Assert: targeted streams have a projection row with balance 100; the three untargeted
    // streams have no projection row at all — rebuild never touched them.
    await using var verifyContext = CreateDbContext();
    var targetedSet = new HashSet<Guid>(targeted);
    foreach (var streamId in streams) {
      var row = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
          .AsNoTracking()
          .FirstOrDefaultAsync(r => r.Id == streamId);
      if (targetedSet.Contains(streamId)) {
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Data.Balance).IsEqualTo(100m);
      } else {
        await Assert.That(row).IsNull();
      }
    }
  }

  /// <summary>
  /// Documents the rebuild-does-not-truncate behavior. If all events for a stream are
  /// removed from wh_event_store after the projection row was written, rebuild does NOT
  /// remove the stale projection row. Stream discovery uses a distinct scan of the event
  /// store, so a stream with zero events is invisible to rebuild — RunAsync is never called
  /// for it — and the projection store is never asked to purge it. The <c>IPerspectiveRebuilder</c>
  /// XML doc claims InPlace "truncates the active table"; the implementation does not. Pinning
  /// the actual behavior here lets a future truncation feature land with a deliberately
  /// failing test to flip.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_DoesNotRemoveProjectionRowsForStreamsWithNoEventsAsync() {
    var orphanStream = Guid.NewGuid();
    var liveStream = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    // Seed both streams with events and project them once so both projection rows exist.
    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, orphanStream, new RebuildCreditedEvent { StreamId = orphanStream, Amount = 42m });
      await _appendEventAsync(eventStore, liveStream, new RebuildCreditedEvent { StreamId = liveStream, Amount = 7m });
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var baseline = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(baseline.Success).IsTrue();
    await Assert.That(baseline.StreamsProcessed).IsEqualTo(2);

    // Simulate an event-store purge: remove all events for orphanStream. liveStream keeps its event.
    await using (var purgeContext = CreateDbContext()) {
      await purgeContext.Set<EventStoreRecord>()
          .Where(e => e.StreamId == orphanStream)
          .ExecuteDeleteAsync();
    }

    // Act: rebuild after the purge.
    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);

    // Assert: only the live stream gets reprojected; rebuild did not see the orphan stream.
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);

    // Assert: the orphan projection row is STILL there — rebuild does not truncate.
    await using var verifyContext = CreateDbContext();
    var orphanRow = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == orphanStream);
    await Assert.That(orphanRow).IsNotNull();
    await Assert.That(orphanRow!.Data.Balance).IsEqualTo(42m);

    var liveRow = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == liveStream);
    await Assert.That(liveRow).IsNotNull();
  }

  /// <summary>
  /// Rebuilding perspective A must not alter perspective B's projection. Pins the
  /// per-perspective isolation semantics — rebuild resolves a single runner from the
  /// registry and only that runner writes to its own perspective store.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_ForSinglePerspective_DoesNotAffectOtherPerspectivesAsync() {
    var balanceStream = Guid.NewGuid();
    var inventoryStream = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    // Seed events for both perspectives.
    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, balanceStream, new RebuildCreditedEvent { StreamId = balanceStream, Amount = 99m });
      await _appendEventAsync(eventStore, inventoryStream, new RebuildStockAdjustedEvent { StreamId = inventoryStream, Delta = 77 });
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    // Project inventory first so it has a baseline row, then mutate that row to a value
    // the event log would never produce. If the subsequent balance rebuild touches inventory,
    // this drift will be overwritten; if rebuild is properly isolated, it stays drifted.
    var inventoryBaseline = await rebuilder.RebuildInPlaceAsync(RebuildInventoryPerspectiveName, CancellationToken.None);
    await Assert.That(inventoryBaseline.Success).IsTrue();
    await Assert.That(inventoryBaseline.StreamsProcessed).IsEqualTo(2);

    const int driftedValue = -12345;
    await using (var corruptContext = CreateDbContext()) {
      await corruptContext.Set<PerspectiveRow<RebuildInventoryModel>>()
          .Where(r => r.Id == inventoryStream)
          .ExecuteUpdateAsync(s => s.SetProperty(
              r => r.Data,
              new RebuildInventoryModel { Id = inventoryStream, OnHand = driftedValue }));
    }

    // Act: rebuild ONLY the balance perspective.
    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);

    // Assert: balance rebuild reports success for the two streams that exist in the store.
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);

    // Assert: inventory row is still drifted — the balance rebuild did not touch it.
    await using var verifyContext = CreateDbContext();
    var inventoryRow = await verifyContext.Set<PerspectiveRow<RebuildInventoryModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == inventoryStream);
    await Assert.That(inventoryRow).IsNotNull();
    await Assert.That(inventoryRow!.Data.OnHand).IsEqualTo(driftedValue);

    // Assert: balance projection reflects its own event stream (99).
    // Note on 198 vs 99: the balance stream was seeded before inventory's baseline rebuild;
    // the inventory rebuild iterates ALL streams in the event store but resolves the balance
    // runner against the InventoryModel store, so it does nothing persistent for the balance
    // stream (the inventory runner doesn't handle RebuildCreditedEvent). Only the explicit
    // balance rebuild produces the 99 row.
    var balanceRow = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == balanceStream);
    await Assert.That(balanceRow).IsNotNull();
    await Assert.That(balanceRow!.Data.Balance).IsEqualTo(99m);
  }
}
