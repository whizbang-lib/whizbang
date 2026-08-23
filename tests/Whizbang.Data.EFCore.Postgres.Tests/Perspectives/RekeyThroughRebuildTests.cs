using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration test proving that an <see cref="IEventUpcaster"/> which re-keys an event's
/// <c>[StreamId]</c> actually re-routes the projection onto the new stream's row during a
/// perspective rebuild.
///
/// <para>Two events are appended on ONE physical stream. The registered <see cref="RekeyUpcaster"/>
/// moves the first onto a different (target) stream and leaves the second on the physical stream.
/// After <c>RebuildInPlaceAsync</c>, the re-keyed event must land on the TARGET row and the
/// untouched event on the PHYSICAL row. Before the runner-seam fix the runner keyed every row by
/// the stored DB <c>stream_id</c>, so both events collapsed onto the physical row and the target row
/// was never created — which this test catches.</para>
/// </summary>
[Category("Integration")]
[Category("Shard2")]
public class RekeyThroughRebuildTests : EFCoreTestBase {

  private const string RekeyPerspectiveName =
      "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RekeyThroughRebuildPerspective";

  /// <summary>
  /// Rebuild DI graph wired exactly like production's innermost layering: the raw Postgres event
  /// store wrapped by <see cref="UpcastingEventStoreDecorator"/> when an upcaster is registered
  /// (mirrors ServiceCollectionExtensions Layer 0). Append writes the old shape; rebuild reads
  /// through the decorator so the upcaster fires on the polymorphic read path.
  /// </summary>
  private ServiceProvider _buildRekeyServices() {
    var services = new ServiceCollection();
    services.AddLogging();

    services.AddScoped<WorkCoordinationDbContext>(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<WorkCoordinationDbContext>());

    // Re-key upcaster + the innermost decorator. AddEventUpcaster registers the singleton pipeline.
    services.AddEventUpcaster<RekeyUpcaster>();
    services.AddScoped<IEventStore>(sp => {
      var raw = new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>());
      var pipeline = sp.GetService<EventUpcasterPipeline>();
      return pipeline is { HasAny: true }
          ? new UpcastingEventStoreDecorator(raw, pipeline)
          : (IEventStore)raw;
    });

    services.AddScoped<IEventStoreQuery>(sp =>
        new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));

    services.AddScoped<IPerspectiveStore<RekeyTestModel>>(sp =>
        new EFCorePostgresPerspectiveStore<RekeyTestModel>(
            sp.GetRequiredService<WorkCoordinationDbContext>(),
            "rekey_test"));

    services.AddScoped<RekeyThroughRebuildPerspective>();

    var asm = typeof(RekeyThroughRebuildTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RekeyThroughRebuildPerspectiveRunner"));

    var registryType = asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" &&
        t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated");
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry), registryType);

    services.AddScoped<IReceptorInvoker>(_ => new NoOpReceptorInvoker());

    services.AddScoped<IPerspectiveCheckpointCompleter>(sp =>
        new EFCorePostgresPerspectiveCheckpointCompleter(
            sp.GetRequiredService<WorkCoordinationDbContext>()));

    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();

    return services.BuildServiceProvider();
  }

  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
  }

  private static async Task _appendEventAsync(IEventStore eventStore, Guid streamId, RekeyTestEvent payload) {
    var envelope = new MessageEnvelope<RekeyTestEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
  }

  [Test]
  public async Task RebuildInPlace_RekeyUpcaster_RoutesEventOntoTargetRowAsync() {
    var physicalStream = Guid.NewGuid();
    var targetStream = Guid.NewGuid();

    await using var sp = _buildRekeyServices();

    // Both events are appended on the SAME physical stream. The first re-keys to targetStream;
    // the second (TargetStreamId == Empty) stays on physicalStream.
    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendEventAsync(eventStore, physicalStream,
          new RekeyTestEvent { StreamId = physicalStream, Marker = "rekeyed", TargetStreamId = targetStream });
      await _appendEventAsync(eventStore, physicalStream,
          new RekeyTestEvent { StreamId = physicalStream, Marker = "stays", TargetStreamId = Guid.Empty });
    }

    // Act — rebuild. The upcaster re-keys the first event on read.
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilderLogger = sp.GetRequiredService<ILogger<PerspectiveRebuilder>>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, rebuilderLogger);

    var result = await rebuilder.RebuildInPlaceAsync(RekeyPerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();

    // Assert — the re-keyed event built the TARGET row; the untouched event built the PHYSICAL row.
    await using var verifyContext = CreateDbContext();

    var targetRow = await verifyContext.Set<PerspectiveRow<RekeyTestModel>>()
        .AsNoTracking().FirstOrDefaultAsync(r => r.Id == targetStream);
    await Assert.That(targetRow).IsNotNull();
    await Assert.That(targetRow!.Data.Marker).IsEqualTo("rekeyed");

    var physicalRow = await verifyContext.Set<PerspectiveRow<RekeyTestModel>>()
        .AsNoTracking().FirstOrDefaultAsync(r => r.Id == physicalStream);
    await Assert.That(physicalRow).IsNotNull();
    await Assert.That(physicalRow!.Data.Marker).IsEqualTo("stays");
  }
}
