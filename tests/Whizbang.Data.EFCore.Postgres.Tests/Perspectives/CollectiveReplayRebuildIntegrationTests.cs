// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// End-to-end proof that a collective mutation survives a perspective rebuild. A collective event is applied
/// live as a set-based SQL UPDATE which the rebuilder never replays; the replay applier folds it back into each
/// stream's per-row rebuild instead. This test seeds two streams + one collective event, rebuilds the
/// perspective from the event store, and asserts the collective effect is reproduced on exactly the target row.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveReplayApplier.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveInMemoryEvaluator.cs</tests>
[Category("Integration")]
[Category("Shard3")]
public class CollectiveReplayRebuildIntegrationTests : EFCoreTestBase {

  private const string RebuildBalancePerspectiveName =
      "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalancePerspective";

  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private ServiceProvider _buildServices() {
    var services = new ServiceCollection();
    services.AddLogging();

    services.AddScoped<WorkCoordinationDbContext>(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<WorkCoordinationDbContext>());

    services.AddScoped<IEventStore>(sp =>
        new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IEventStoreQuery>(sp =>
        new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));

    services.AddScoped<IPerspectiveStore<RebuildBalanceModel>>(sp =>
        new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(
            sp.GetRequiredService<WorkCoordinationDbContext>(), "wh_per_rebuild_balance"));

    services.AddScoped<RebuildBalancePerspective>();
    services.AddScoped<RebuildBalanceCollectiveHandler>();

    var asm = typeof(CollectiveReplayRebuildIntegrationTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildBalancePerspectiveRunner"));
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry),
        asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" &&
            t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated"));

    services.AddScoped<IReceptorInvoker>(_ => new NoOpReceptorInvoker());
    services.AddScoped<IPerspectiveCheckpointCompleter>(sp =>
        new EFCorePostgresPerspectiveCheckpointCompleter(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();

    // Collective consume + replay wiring. Entries come from the generated registry in this assembly.
    services.AddCollectiveEventsEFCore<WorkCoordinationDbContext>(_collectiveEntries(asm));
    services.AddCollectiveExecutorEFCore<RebuildBalanceModel>();

    return services.BuildServiceProvider();
  }

  // The source generator emits `public static readonly ... Entries` on CollectiveApplyRegistry (namespace
  // Whizbang.Core.Generated) into THIS assembly — resolve it from the test assembly's own types (Core's copy
  // is empty) and read the field.
  private static IReadOnlyList<CollectiveApplyEntry> _collectiveEntries(System.Reflection.Assembly asm) {
    var registry = asm.GetTypes().Single(t => t.Name == "CollectiveApplyRegistry");
    var entries = registry.GetField("Entries")!.GetValue(null)!;
    return (IReadOnlyList<CollectiveApplyEntry>)entries;
  }

  private static async Task _appendAsync<TEvent>(IEventStore eventStore, Guid streamId, TEvent payload, Guid? eventId = null)
      where TEvent : IEvent {
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = eventId is { } id ? new MessageId(id) : MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
  }

  [Test]
  public async Task Rebuild_FoldsCollectiveEvent_OnlyIntoTheTargetRowAsync() {
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    var collectiveStream = Guid.NewGuid();

    await using var sp = _buildServices();

    // Seed: A credited 100, B credited 500, then a collective event that resets A's balance to 0.
    // The collective event is appended LAST so its message id sorts after the credits — it must interleave
    // chronologically and win on stream A.
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 100m });
      await _appendAsync(eventStore, streamB, new RebuildCreditedEvent { StreamId = streamB, Amount = 500m });
      await _appendAsync(eventStore, collectiveStream, new RebuildResetBalanceCollectiveEvent {
        StreamId = collectiveStream,
        Scope = new TenantCollectiveScope(string.Empty),
        TargetId = streamA,
        ResetTo = 0m,
      });
    }

    // Act: rebuild the perspective from the event store.
    var rebuilder = sp.GetRequiredService<IPerspectiveRebuilder>();
    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");

    // Assert: the collective reset was folded into stream A (0), stream B untouched (500).
    await using (var scope = sp.CreateAsyncScope()) {
      var store = scope.ServiceProvider.GetRequiredService<IPerspectiveStore<RebuildBalanceModel>>();
      var a = await store.GetByStreamIdAsync(streamA, CancellationToken.None);
      var b = await store.GetByStreamIdAsync(streamB, CancellationToken.None);

      await Assert.That(a).IsNotNull();
      await Assert.That(a!.Balance).IsEqualTo(0m)
        .Because("the collective reset event was interleaved after the credit and folded into stream A on rebuild");
      await Assert.That(b).IsNotNull();
      await Assert.That(b!.Balance).IsEqualTo(500m)
        .Because("the collective event's Where targets only stream A — stream B must be unaffected");
    }
  }

  [Test]
  public async Task Rebuild_WithCollectivesInTheTenant_FoldsTheStreamInCommitOrderAsync() {
    // The stream's first committed event carries its higher id. With a collective in the tenant, the interleave used
    // to re-sort the whole list by id, fold the first commit last, and write the row at a stale position (#1346).
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    var collectiveStream = Guid.NewGuid();
    var lowerId = Guid.CreateVersion7();
    var higherId = Guid.CreateVersion7();

    await using var sp = _buildServices();

    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      var collectiveId = Guid.CreateVersion7();
      await _appendAsync(eventStore, collectiveStream, new RebuildResetBalanceCollectiveEvent {
        StreamId = collectiveStream,
        Scope = new TenantCollectiveScope(string.Empty),
        TargetId = streamB,
        ResetTo = 0m,
      }, collectiveId);
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 100m }, higherId);
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 5m }, lowerId);

      // Commit order: the collective, then the higher id, then the lower id.
      var db = scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
      await db.Database.ExecuteSqlRawAsync(
        "UPDATE wh_event_store SET commit_sequence = CASE event_id WHEN {0} THEN 1 WHEN {1} THEN 2 WHEN {2} THEN 3 END " +
        "WHERE event_id IN ({0}, {1}, {2})", collectiveId, higherId, lowerId);
    }

    var rebuilder = sp.GetRequiredService<IPerspectiveRebuilder>();
    var result = await rebuilder.RebuildInPlaceAsync(RebuildBalancePerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");

    await using (var scope = sp.CreateAsyncScope()) {
      var store = scope.ServiceProvider.GetRequiredService<IPerspectiveStore<RebuildBalanceModel>>();
      var metadata = await store.GetMetadataByStreamIdAsync(streamA, CancellationToken.None);

      await Assert.That(metadata).IsNotNull();
      await Assert.That(metadata!.EventId).IsEqualTo(lowerId.ToString("D"))
        .Because("the stream's last committed event folds last, though its id is the lower one");
      await Assert.That(metadata.CommitSequence).IsEqualTo(3L)
        .Because("the row is written at the stream's head position, which the ordering guard admits next time");
    }
  }
}
