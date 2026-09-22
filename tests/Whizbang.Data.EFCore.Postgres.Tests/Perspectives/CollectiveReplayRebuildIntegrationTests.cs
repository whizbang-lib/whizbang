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
            sp.GetRequiredService<WorkCoordinationDbContext>(), "rebuild_balance"));

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

  private static async Task _appendAsync<TEvent>(IEventStore eventStore, Guid streamId, TEvent payload)
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
}
