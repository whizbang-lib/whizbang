using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
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
  /// Thread-local cell that lets scenario 4 inject a single poison stream without rebuilding
  /// the whole DI graph. When non-empty, the wrapping event store (see _buildRebuildServices)
  /// throws on ReadPolymorphicAsync for any stream in the set, causing the generated runner's
  /// event-load step to propagate the exception up to PerspectiveRebuilder — which should
  /// then catch it per-stream without aborting the batch.
  /// </summary>
  private readonly HashSet<Guid> _poisonStreams = [];

  /// <summary>
  /// Shared capture list for scenario 3. Every receptor invocation the runner triggers during
  /// a rebuild appends (stage, ambient-processing-mode) here, letting the test assert that the
  /// ambient mode propagates into the lifecycle context exactly as <see cref="PerspectiveRebuilder"/>
  /// sets it. Lives on the test class so it survives across the scoped invoker instances the
  /// runner creates per lifecycle stage.
  /// </summary>
  private readonly List<(LifecycleStage Stage, ProcessingMode? Mode)> _receptorInvocations = [];
  private readonly Lock _receptorInvocationsLock = new();

  /// <summary>
  /// Test-only IReceptorInvoker that records what stage was invoked and the ProcessingMode
  /// carried in the lifecycle context. Used by scenario 3 to prove PerspectiveRebuilder's
  /// ambient mode is visible at the receptor-invocation boundary.
  /// </summary>
  private sealed class RecordingReceptorInvoker(
      List<(LifecycleStage Stage, ProcessingMode? Mode)> sink,
      Lock sinkLock) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      var mode = (context as LifecycleExecutionContext)?.ProcessingMode;
      lock (sinkLock) {
        sink.Add((stage, mode));
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Event store decorator that forwards every operation to the wrapped store except
  /// ReadPolymorphicAsync for streams in <paramref name="poisonStreams"/>, which throw.
  /// Reused from scenario 4. Kept inside the test class since it has no production value.
  /// </summary>
  private sealed class PoisonEventStore(IEventStore inner, HashSet<Guid> poisonStreams) : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken ct = default) =>
        inner.AppendAsync(streamId, envelope, ct);
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken ct = default) where TMessage : notnull =>
        inner.AppendAsync(streamId, message, ct);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken ct = default) =>
        inner.ReadAsync<TMessage>(streamId, fromSequence, ct);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken ct = default) =>
        inner.ReadAsync<TMessage>(streamId, fromEventId, ct);

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
      if (poisonStreams.Contains(streamId)) {
        throw new InvalidOperationException($"Simulated read failure for poison stream {streamId}");
      }
      await foreach (var env in inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, ct)) {
        yield return env;
      }
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken ct = default) =>
        inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, ct);
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) =>
        inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, ct);
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken ct = default) =>
        inner.GetLastSequenceAsync(streamId, ct);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
        inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }

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

    // Real Postgres event store — AppendAsync writes to wh_event_store.
    // Wrapped in PoisonEventStore so scenario 4 can inject read failures for a specific
    // stream by adding its ID to _poisonStreams; a normal run leaves the set empty.
    services.AddScoped<IEventStore>(sp =>
        new PoisonEventStore(
            new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>()),
            _poisonStreams));

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

    // Recording invoker — observes ProcessingMode seen by lifecycle receptor invocation.
    services.AddScoped<IReceptorInvoker>(_ => new RecordingReceptorInvoker(_receptorInvocations, _receptorInvocationsLock));

    // Cursor persistence: matches what PostgresDriverExtensions.Postgres registers.
    services.AddScoped<IPerspectiveCheckpointCompleter>(sp =>
        new EFCorePostgresPerspectiveCheckpointCompleter(
            sp.GetRequiredService<WorkCoordinationDbContext>()));

    // IPerspectiveRebuilder — normally registered by AddWhizbang, re-added here because
    // the hand-built DI container skips AddWhizbang.
    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();

    return services.BuildServiceProvider();
  }

  private static async Task _appendEventAsync<TEvent>(IEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    _ = await _appendAndGetEventIdAsync(eventStore, streamId, payload);
  }

  private static async Task<Guid> _appendAndGetEventIdAsync<TEvent>(IEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
    return messageId.Value;
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

    // Simulate an event-store purge: remove all events for orphanStream. liveStream keeps its
    // event. We first delete the cursor row — wh_perspective_cursors.last_event_id has a FK to
    // wh_event_store.event_id now that rebuild persists cursors, so deleting events would fail
    // if the cursor still references them.
    await using (var purgeContext = CreateDbContext()) {
      await purgeContext.Set<PerspectiveCursorRecord>()
          .Where(c => c.StreamId == orphanStream && c.PerspectiveName == RebuildBalancePerspectiveName)
          .ExecuteDeleteAsync();
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

  /// <summary>
  /// A single stream failing mid-rebuild must not abort the remaining streams. PerspectiveRebuilder
  /// wraps each runner.RunAsync call in a try/catch and logs per-stream failures. The batch-level
  /// Success flag is still true and StreamsProcessed reflects the successful streams only. Pins
  /// that behavior so any future "fail the batch on first error" change lands deliberately with
  /// this test flipping.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_ContinuesAfterPerStreamFailureAsync() {
    var streams = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    var poisonStream = streams[1];
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      foreach (var streamId in streams) {
        await _appendEventAsync(eventStore, streamId, new RebuildCreditedEvent { StreamId = streamId, Amount = 10m });
      }
    }

    // Mark poisonStream as a read-failure target. The wrapper throws on ReadPolymorphicAsync
    // for this ID; the runner's event-load path has no try/catch around the enumeration, so
    // the exception propagates to PerspectiveRebuilder's per-stream catch.
    _poisonStreams.Add(poisonStream);

    try {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
      var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

      var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);

      // Rebuilder treats per-stream failures as non-fatal — Success is unconditional after
      // the loop completes (see PerspectiveRebuilder.cs:111).
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.StreamsProcessed).IsEqualTo(3);
      await Assert.That(result.Error).IsNull();
    } finally {
      _poisonStreams.Remove(poisonStream);
    }

    // Assert: the three non-poison streams have projection rows; the poison stream does not.
    await using var verifyContext = CreateDbContext();
    foreach (var streamId in streams) {
      var row = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
          .AsNoTracking()
          .FirstOrDefaultAsync(r => r.Id == streamId);
      if (streamId == poisonStream) {
        await Assert.That(row).IsNull();
      } else {
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Data.Balance).IsEqualTo(10m);
      }
    }
  }

  /// <summary>
  /// Rebuild must re-project a stream when its projection row has been removed (e.g., via a
  /// direct admin purge) even though the stream's events are still in the event store.
  /// Exercises the "currentModel == null at runner start" branch with a cursor that already
  /// exists from the prior projection — rebuild must not be confused by a stale cursor into
  /// skipping the replay.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_RecreatesMissingProjectionRowAsync() {
    var streamId = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, streamId, new RebuildCreditedEvent { StreamId = streamId, Amount = 250m });
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    // Baseline project — creates projection row AND cursor.
    var baseline = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(baseline.Success).IsTrue();
    await Assert.That(baseline.StreamsProcessed).IsEqualTo(1);

    // Simulate admin purge — delete the projection row, leave the cursor in place.
    await using (var purgeContext = CreateDbContext()) {
      await purgeContext.Set<PerspectiveRow<RebuildBalanceModel>>()
          .Where(r => r.Id == streamId)
          .ExecuteDeleteAsync();
    }

    // Confirm the row is gone.
    await using (var midContext = CreateDbContext()) {
      var missing = await midContext.Set<PerspectiveRow<RebuildBalanceModel>>()
          .AsNoTracking()
          .FirstOrDefaultAsync(r => r.Id == streamId);
      await Assert.That(missing).IsNull();
    }

    // Act: rebuild again. Expectation: row is recreated from the event log.
    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);

    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<RebuildBalanceModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == streamId);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Balance).IsEqualTo(250m);
  }

  /// <summary>
  /// PerspectiveRebuilder sets <c>ProcessingModeAccessor.Current = ProcessingMode.Rebuild</c>
  /// for the duration of the replay. Generated runners copy that ambient value into the
  /// <see cref="LifecycleExecutionContext"/> they pass to every <see cref="IReceptorInvoker"/>
  /// call. The real <see cref="ReceptorInvoker"/> consults <c>context.ProcessingMode</c> to
  /// suppress receptors that lack <see cref="FireDuringReplayAttribute"/>. This test pins the
  /// contract between rebuilder and invoker: the ambient mode arrives at the invocation site.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_SetsProcessingModeOnLifecycleContextAsync() {
    var streamId = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, streamId, new RebuildCreditedEvent { StreamId = streamId, Amount = 1m });
    }

    _receptorInvocations.Clear();

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();

    // Snapshot under the lock so the async background lifecycle tasks the runner queues
    // (e.g., PrePerspectiveDetached via Task.Run) don't race with the assertion.
    List<(LifecycleStage Stage, ProcessingMode? Mode)> captured;
    lock (_receptorInvocationsLock) {
      captured = [.. _receptorInvocations];
    }

    // At least one lifecycle receptor invocation must have happened for the stream we seeded.
    await Assert.That(captured.Count).IsGreaterThan(0);

    // Every captured invocation carries the rebuild mode — not null, not Replay, not Live.
    foreach (var (stage, mode) in captured) {
      await Assert.That(mode).IsEqualTo(ProcessingMode.Rebuild);
    }
  }

  /// <summary>
  /// After in-place rebuild, the cursor for each replayed stream must exist in
  /// <c>wh_perspective_cursors</c> with <c>Status = Completed</c> and <c>LastEventId</c>
  /// equal to the final replayed event's UUIDv7. Pairs with the runner's return value —
  /// PerspectiveRebuilder now persists the PerspectiveCursorCompletion through
  /// IPerspectiveCheckpointCompleter rather than discarding it.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_WritesCursorCheckpointAsync() {
    var streamId = Guid.NewGuid();
    Guid lastEventId;
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      lastEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 1m });
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(cursor.LastEventId).IsEqualTo(lastEventId);
  }

  /// <summary>
  /// RebuildStreamsAsync writes cursors for the targeted streams only. Untargeted streams
  /// stay cursor-less if they had no prior cursor (matches scenario 2's projection-row claim).
  /// </summary>
  [Test]
  public async Task RebuildStreamsAsync_WritesCursorsForTargetedStreamsOnlyAsync() {
    var streams = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    var eventIds = new Dictionary<Guid, Guid>(streams.Length);
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      foreach (var streamId in streams) {
        eventIds[streamId] = await _appendAndGetEventIdAsync(eventStore, streamId,
            new RebuildCreditedEvent { StreamId = streamId, Amount = 10m });
      }
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var targeted = new[] { streams[1], streams[3] };
    var result = await rebuilder.RebuildStreamsAsync(RebuildBalancePerspectiveName, targeted, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);

    await using var verifyContext = CreateDbContext();
    var targetedSet = new HashSet<Guid>(targeted);
    foreach (var streamId in streams) {
      var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
          .AsNoTracking()
          .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
      if (targetedSet.Contains(streamId)) {
        await Assert.That(cursor).IsNotNull();
        await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
        await Assert.That(cursor.LastEventId).IsEqualTo(eventIds[streamId]);
      } else {
        await Assert.That(cursor).IsNull();
      }
    }
  }

  /// <summary>
  /// Blue-green rebuild must also leave the cursor Completed at the final event. Even though
  /// <c>RebuildBlueGreenAsync</c> currently aliases to in-place (no real table-swap yet), pin
  /// the cursor contract so a future blue-green implementation can't regress it.
  /// </summary>
  [Test]
  public async Task RebuildBlueGreenAsync_WritesCursorCheckpointAsync() {
    var streamId = Guid.NewGuid();
    Guid lastEventId;
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      lastEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 5m });
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildBlueGreenAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(cursor.LastEventId).IsEqualTo(lastEventId);
  }

  /// <summary>
  /// Cursor LastEventId must equal the FINAL event in the stream — not the first, not an
  /// intermediate one. Proves the rebuilder persists the completion after replay is done
  /// rather than mid-stream.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_CursorLastEventIdMatchesFinalEventAsync() {
    var streamId = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    var appendedIds = new List<Guid>();
    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      appendedIds.Add(await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 10m }));
      appendedIds.Add(await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildDebitedEvent { StreamId = streamId, Amount = 3m }));
      appendedIds.Add(await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 7m }));
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    // The last appended event — events after it don't exist — so LastEventId must match appendedIds[^1].
    await Assert.That(cursor!.LastEventId).IsEqualTo(appendedIds[^1]);
  }

  /// <summary>
  /// When a stream's runner throws (via the PoisonEventStore decorator), that stream's cursor
  /// must NOT be persisted. The rebuilder treats per-stream failures as non-fatal but the
  /// failed stream's checkpoint never gets flushed — matches the pre-existing "Success overall,
  /// failing stream's projection row unchanged" invariant.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_FailedStreamDoesNotWriteCursorAsync() {
    var streams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
    var poisonStream = streams[1];
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      foreach (var streamId in streams) {
        await _appendEventAsync(eventStore, streamId, new RebuildCreditedEvent { StreamId = streamId, Amount = 1m });
      }
    }

    _poisonStreams.Add(poisonStream);
    try {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
      var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

      var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    } finally {
      _poisonStreams.Remove(poisonStream);
    }

    await using var verifyContext = CreateDbContext();
    foreach (var streamId in streams) {
      var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
          .AsNoTracking()
          .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
      if (streamId == poisonStream) {
        await Assert.That(cursor).IsNull();
      } else {
        await Assert.That(cursor).IsNotNull();
        await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
      }
    }
  }

  /// <summary>
  /// Reproduces the production scenario: cursor already exists (e.g., stuck in Processing
  /// status from a crashed live instance). Rebuild must UPDATE that row's status, last_event_id,
  /// and clear error/rewind flags. Exercises the ON CONFLICT DO UPDATE path of the UPSERT —
  /// the other cursor tests start clean and hit the INSERT path.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_WithExistingProcessingCursor_UpdatesToCompletedAsync() {
    var streamId = Guid.NewGuid();
    Guid firstEventId;
    Guid secondEventId;
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      firstEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 10m });
      secondEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 20m });
    }

    // Simulate the production pre-existing-cursor case: a row stuck in Processing,
    // pointing at the first event, with a stale error message. Raw SQL so we can also
    // set the rewind columns (they're table columns but the PerspectiveCursorRecord
    // EF entity doesn't expose them).
    await using (var seedContext = CreateDbContext()) {
      await seedContext.Database.ExecuteSqlRawAsync(
          @"INSERT INTO wh_perspective_cursors
              (stream_id, perspective_name, last_event_id, status, processed_at, error,
               rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at)
            VALUES ({0}, {1}, {2}, {3}, NOW() - INTERVAL '1 hour', {4}, {2}, NOW() - INTERVAL '1 hour', NOW() - INTERVAL '1 hour')",
          streamId,
          RebuildBalancePerspectiveName,
          firstEventId,
          (short)PerspectiveProcessingStatus.Processing,
          "prior worker crashed mid-processing");
    }

    // Confirm the stuck state is there before the rebuild.
    await using (var midContext = CreateDbContext()) {
      var pre = await midContext.Set<PerspectiveCursorRecord>()
          .AsNoTracking()
          .FirstAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
      await Assert.That(pre.Status).IsEqualTo(PerspectiveProcessingStatus.Processing);
      await Assert.That(pre.LastEventId).IsEqualTo(firstEventId);
      await Assert.That(pre.Error).IsNotNull();
    }

    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);

    // Cursor MUST reflect the rebuilt end-state: Completed status, LastEventId = final event,
    // error cleared. Rewind fields are cleared by the completer but the EF entity doesn't
    // expose them — a separate raw query confirms those below.
    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(cursor.LastEventId).IsEqualTo(secondEventId);
    await Assert.That(cursor.Error).IsNull();

    await using var rewindConnection = verifyContext.Database.GetDbConnection();
    await rewindConnection.OpenAsync();
    await using var cmd = rewindConnection.CreateCommand();
    cmd.CommandText = @"SELECT rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at
                        FROM wh_perspective_cursors
                        WHERE stream_id = @stream_id AND perspective_name = @perspective_name";
    var streamParam = cmd.CreateParameter();
    streamParam.ParameterName = "@stream_id";
    streamParam.Value = streamId;
    cmd.Parameters.Add(streamParam);
    var nameParam = cmd.CreateParameter();
    nameParam.ParameterName = "@perspective_name";
    nameParam.Value = RebuildBalancePerspectiveName;
    cmd.Parameters.Add(nameParam);
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.IsDBNull(0)).IsTrue();
    await Assert.That(reader.IsDBNull(1)).IsTrue();
    await Assert.That(reader.IsDBNull(2)).IsTrue();
  }

  // ==========================================================================
  // RebuildPerspectiveCommandReceptor end-to-end integration tests
  //
  // These exercise the full command → receptor → rebuilder → completer → Postgres
  // chain against the same DI wiring used by the rebuilder tests above, proving
  // that dispatching RebuildPerspectiveCommand actually updates wh_perspective_cursors.
  // Pins the fix for the production report: "dispatching RebuildPerspectiveCommand
  // has no effect because there's no receptor".
  // ==========================================================================

  [Test]
  public async Task RebuildPerspectiveCommand_WithNamedPerspective_UpdatesCursorAsync() {
    var streamId = Guid.NewGuid();
    Guid lastEventId;
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      lastEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 42m });
    }

    var receptor = new Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor>>());

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
        PerspectiveNames: [RebuildBalancePerspectiveName]));

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(cursor.LastEventId).IsEqualTo(lastEventId);
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithModeInPlace_UpdatesCursorAsync() {
    var streamId = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 5m });
    }

    var receptor = new Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor>>());

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
        PerspectiveNames: [RebuildBalancePerspectiveName],
        Mode: RebuildMode.InPlace));

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithIncludeStreamIds_UpdatesOnlyTargetedCursorsAsync() {
    var streams = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      foreach (var streamId in streams) {
        await _appendEventAsync(eventStore, streamId,
            new RebuildCreditedEvent { StreamId = streamId, Amount = 10m });
      }
    }

    var receptor = new Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor>>());

    var targeted = new[] { streams[0], streams[2] };
    await receptor.HandleAsync(new RebuildPerspectiveCommand(
        PerspectiveNames: [RebuildBalancePerspectiveName],
        IncludeStreamIds: targeted));

    await using var verifyContext = CreateDbContext();
    var targetedSet = new HashSet<Guid>(targeted);
    foreach (var streamId in streams) {
      var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
          .AsNoTracking()
          .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
      if (targetedSet.Contains(streamId)) {
        await Assert.That(cursor).IsNotNull();
        await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
      } else {
        await Assert.That(cursor).IsNull();
      }
    }
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithNullPerspectiveNames_FansOutToAllRegisteredAsync() {
    var balanceStream = Guid.NewGuid();
    var inventoryStream = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, balanceStream,
          new RebuildCreditedEvent { StreamId = balanceStream, Amount = 1m });
      await _appendEventAsync(eventStore, inventoryStream,
          new RebuildStockAdjustedEvent { StreamId = inventoryStream, Delta = 1 });
    }

    var receptor = new Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor>>());

    await receptor.HandleAsync(new RebuildPerspectiveCommand(PerspectiveNames: null));

    await using var verifyContext = CreateDbContext();
    // Balance perspective has a cursor on its stream.
    var balanceCursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == balanceStream && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(balanceCursor).IsNotNull();
    // Inventory perspective has a cursor on its stream.
    var inventoryCursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == inventoryStream && c.PerspectiveName == RebuildInventoryPerspectiveName);
    await Assert.That(inventoryCursor).IsNotNull();
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithPreexistingProcessingCursor_FlipsToCompletedAsync() {
    // Mirrors the production scenario but drives it through the receptor instead of calling
    // IPerspectiveRebuilder directly. Pins that the command path actually unsticks the cursor.
    var streamId = Guid.NewGuid();
    Guid firstEventId;
    Guid secondEventId;
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      firstEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 10m });
      secondEventId = await _appendAndGetEventIdAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 20m });
    }

    await using (var seedContext = CreateDbContext()) {
      await seedContext.Database.ExecuteSqlRawAsync(
          @"INSERT INTO wh_perspective_cursors
              (stream_id, perspective_name, last_event_id, status, processed_at, error)
            VALUES ({0}, {1}, {2}, {3}, NOW() - INTERVAL '1 hour', {4})",
          streamId,
          RebuildBalancePerspectiveName,
          firstEventId,
          (short)PerspectiveProcessingStatus.Processing,
          "prior worker crashed mid-processing");
    }

    var receptor = new Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor>>());

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
        PerspectiveNames: [RebuildBalancePerspectiveName]));

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(cursor.LastEventId).IsEqualTo(secondEventId);
    await Assert.That(cursor.Error).IsNull();
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithFromEventIdSet_IgnoresItAndStillRebuildsAsync() {
    var streamId = Guid.NewGuid();
    await using var sp = _buildRebuildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, streamId,
          new RebuildCreditedEvent { StreamId = streamId, Amount = 1m });
    }

    var receptor = new Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Whizbang.Data.EFCore.Postgres.RebuildPerspectiveCommandReceptor>>());

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
        PerspectiveNames: [RebuildBalancePerspectiveName],
        FromEventId: 9999));

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>()
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.StreamId == streamId && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
  }
}
