// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
            "wh_per_rekey_test"));

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

    // Both events are appended on the SAME physical stream. The first re-keys to targetStream —
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

  // ── #1820: a row that receives re-keyed events is folded from its own stream and every source ──────────────────
  // Each event is stamped with the next commit sequence as it is appended, so append order is commit order. The
  // perspective accumulates markers, so the row shows exactly which events it folded and in what order.

  private async Task<Guid> _appendCommittedAsync(ServiceProvider sp, Guid storedOn, string marker, Guid rekeyTo = default, bool replace = false) {
    var id = MessageId.New();
    await using (var scope = sp.CreateAsyncScope()) {
      await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(storedOn, new MessageEnvelope<RekeyTestEvent> {
        MessageId = id,
        Payload = new RekeyTestEvent { StreamId = storedOn, Marker = marker, TargetStreamId = rekeyTo, ReplaceInstance = replace },
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = []
      });
    }
    await using var stamp = CreateDbContext();
    await stamp.Database.ExecuteSqlRawAsync(
      "UPDATE wh_event_store SET commit_sequence = (SELECT COALESCE(MAX(commit_sequence), 0) + 1 FROM wh_event_store) WHERE event_id = {0}",
      id.Value);
    return id.Value;
  }

  private async Task<PerspectiveRow<RekeyTestModel>> _rebuiltRowAsync(ServiceProvider sp, Guid row) {
    var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<PerspectiveRebuilder>>());
    var result = await rebuilder.RebuildInPlaceAsync(RekeyPerspectiveName, CancellationToken.None);
    await Assert.That(result.Success).IsTrue();
    await using var verify = CreateDbContext();
    var found = await verify.Set<PerspectiveRow<RekeyTestModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == row);
    await Assert.That(found).IsNotNull().Because("the row the events were re-keyed onto is rebuilt");
    return found!;
  }

  private async Task<long> _commitSequenceOfAsync(Guid eventId) {
    await using var read = CreateDbContext();
    return await read.Database
      .SqlQueryRaw<long>("SELECT commit_sequence AS \"Value\" FROM wh_event_store WHERE event_id = {0}", eventId)
      .SingleAsync();
  }

  private async Task _assertPositionedOnAsync(PerspectiveRow<RekeyTestModel> row, Guid lastEvent) {
    await Assert.That(row.Metadata.EventId).IsEqualTo(lastEvent.ToString())
      .Because("the row records the event it folded last, the one committed last");
    await Assert.That(row.Metadata.CommitSequence).IsEqualTo(await _commitSequenceOfAsync(lastEvent));
  }

  [Test]
  public async Task Rebuild_RekeyOntoAnEmptyStream_FoldsEveryRekeyedEventInCommitOrderAsync() {
    var source = Guid.NewGuid();
    var target = Guid.NewGuid();
    await using var sp = _buildRekeyServices();
    await _appendCommittedAsync(sp, source, "a", target);
    var last = await _appendCommittedAsync(sp, source, "b", target);

    var row = await _rebuiltRowAsync(sp, target);

    await Assert.That(row.Data.Marker).IsEqualTo("a|b");
    await _assertPositionedOnAsync(row, last);
  }

  [Test]
  public async Task Rebuild_RekeyOntoAStreamWithEarlierEvents_KeepsTheTargetsOwnHistoryAsync() {
    var source = Guid.NewGuid();
    var target = Guid.NewGuid();
    await using var sp = _buildRekeyServices();
    await _appendCommittedAsync(sp, target, "t1");
    await _appendCommittedAsync(sp, target, "t2");
    var last = await _appendCommittedAsync(sp, source, "p1", target);

    var row = await _rebuiltRowAsync(sp, target);

    await Assert.That(row.Data.Marker).IsEqualTo("t1|t2|p1")
      .Because("the target's own events are part of its history; folding the re-keyed event alone lost them");
    await _assertPositionedOnAsync(row, last);
  }

  [Test]
  public async Task Rebuild_RekeyOntoAStreamWithLaterEvents_FoldsTheRekeyedEventFirstAsync() {
    var source = Guid.NewGuid();
    var target = Guid.NewGuid();
    await using var sp = _buildRekeyServices();
    await _appendCommittedAsync(sp, source, "p1", target);
    await _appendCommittedAsync(sp, target, "t1");
    var last = await _appendCommittedAsync(sp, target, "t2");

    var row = await _rebuiltRowAsync(sp, target);

    await Assert.That(row.Data.Marker).IsEqualTo("p1|t1|t2")
      .Because("the re-keyed event was committed before the target's own, so it is folded first");
    await _assertPositionedOnAsync(row, last);
  }

  [Test]
  public async Task Rebuild_TwoSourcesRekeyedOntoOneTarget_FoldsBothWithTheTargetsOwnAsync() {
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    var target = Guid.NewGuid();
    await using var sp = _buildRekeyServices();
    await _appendCommittedAsync(sp, target, "t0");
    await _appendCommittedAsync(sp, first, "a", target);
    await _appendCommittedAsync(sp, second, "b", target);
    await _appendCommittedAsync(sp, target, "t1");
    var last = await _appendCommittedAsync(sp, first, "c", target);

    var row = await _rebuiltRowAsync(sp, target);

    await Assert.That(row.Data.Marker).IsEqualTo("t0|a|b|t1|c")
      .Because("every source re-keyed onto the row is folded with its own events, whichever stream is read last");
    await _assertPositionedOnAsync(row, last);
  }

  [Test]
  public async Task Rebuild_RekeyByAnUpcasterThatReplacesTheEvent_IsMergedLikeAnInPlaceOneAsync() {
    var source = Guid.NewGuid();
    var target = Guid.NewGuid();
    await using var sp = _buildRekeyServices();
    await _appendCommittedAsync(sp, target, "t1");
    await _appendCommittedAsync(sp, source, "p1", target, replace: true);
    var last = await _appendCommittedAsync(sp, source, "p2", target);

    var row = await _rebuiltRowAsync(sp, target);

    await Assert.That(row.Data.Marker).IsEqualTo("t1|p1|p2")
      .Because("a re-key is a re-key whether the upcaster changes the event in place or returns a new one");
    await _assertPositionedOnAsync(row, last);
  }
}
