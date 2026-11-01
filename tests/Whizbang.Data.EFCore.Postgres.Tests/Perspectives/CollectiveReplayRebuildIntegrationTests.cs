// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

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

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  // withArchive: the event store reads through the table archive, as the driver's registration chains it, and the
  // database is reachable the way the driver registers it for archive work.
  private ServiceProvider _buildServices(bool withArchive = false, bool withSnapshots = false) {
    var services = new ServiceCollection();
    services.AddLogging();

    services.AddScoped<WorkCoordinationDbContext>(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<WorkCoordinationDbContext>());

    services.AddScoped<IEventStore>(sp => {
      var driver = new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>());
      return withArchive ? new ArchiveAwareEventStoreDecorator(driver, _archive(), readThrough: true) : driver;
    });
    if (withArchive) {
      var database = new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null);
      services.AddSingleton(database);
      services.AddSingleton<IArchivedStreamExtents>(new PostgresArchivedStreamExtents(database));
    }
    services.AddScoped<IEventStoreQuery>(sp =>
        new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));
    if (withSnapshots) {
      services.AddSingleton<IPerspectiveSnapshotStore>(new EFCorePerspectiveSnapshotStore(NpgsqlDataSource.Create(ConnectionString)));
    }

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

  // #1932 and #1931: a collective is its own stream, and once that stream is archived whole the hot store holds none of
  // it. The collective is the newest event of the tenant here, so it is the last event folded into stream A's row.
  [Test]
  public async Task Rebuild_IncludeAll_FoldsACollectiveArchivedWhole_ThatIsTheRowsLastEvent_AndCheckpointsOnTheRowsOwnEventAsync() {
    var (sp, streamA, streamB, creditA) = await _archivedCollectiveIsTheLastEventAsync();
    await using var _ = sp;

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildInPlaceAsync(
      RebuildBalancePerspectiveName, new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll }, CancellationToken.None);

    await Assert.That(result.Error).IsNull()
      .Because("the cursor of a row must name an event of its own stream the hot store holds, never the archived collective");
    await Assert.That(result.Success).IsTrue();
    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(0m)
      .Because("the archived collective is listed from the lifecycle ledger and read back through the archive");
    await Assert.That(await _balanceAsync(sp, streamB)).IsEqualTo(500m).Because("the collective targets stream A only");
    await Assert.That(await _cursorAsync(streamA)).IsEqualTo(creditA)
      .Because("a collective folds into the row but does not move its checkpoint, as on the live path");
  }

  [Test]
  public async Task Rebuild_ByDefault_LeavesOutACollectiveArchivedWhole_AndSucceedsAsync() {
    var (sp, streamA, streamB, creditA) = await _archivedCollectiveIsTheLastEventAsync();
    await using var _ = sp;

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildInPlaceAsync(
      RebuildBalancePerspectiveName, CancellationToken.None);

    await Assert.That(result.Error).IsNull();
    await Assert.That(result.Success).IsTrue();
    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(100m)
      .Because("a rebuild reads no archived data by default, and the collective's stream is archived whole");
    await Assert.That(await _balanceAsync(sp, streamB)).IsEqualTo(500m);
    await Assert.That(await _cursorAsync(streamA)).IsEqualTo(creditA);
  }

  // Streams A and B credited, then a collective that resets A, archived whole: the tenant's newest event.
  private async Task<(ServiceProvider Services, Guid StreamA, Guid StreamB, Guid CreditA)> _archivedCollectiveIsTheLastEventAsync() {
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    var collectiveStream = Guid.NewGuid();
    var creditA = Guid.CreateVersion7();
    var sp = _buildServices(withArchive: true);
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 100m }, creditA);
      await _appendAsync(eventStore, streamB, new RebuildCreditedEvent { StreamId = streamB, Amount = 500m });
      await _appendAsync(eventStore, collectiveStream, new RebuildResetBalanceCollectiveEvent {
        StreamId = collectiveStream,
        Scope = new TenantCollectiveScope(string.Empty),
        TargetId = streamA,
        ResetTo = 0m,
      });
    }
    var archived = await _archive().WriteAsync(ArchiveBatch.WholeStream(collectiveStream));
    await Assert.That(archived.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_event_store WHERE stream_id = $1", conn);
    cmd.Parameters.AddWithValue(collectiveStream);
    await Assert.That((long)(await cmd.ExecuteScalarAsync())!).IsEqualTo(0L)
      .Because("the control: the collective's stream is archived whole, so the hot store holds none of it");
    return (sp, streamA, streamB, creditA);
  }

  private static async Task<decimal?> _balanceAsync(ServiceProvider sp, Guid stream) {
    await using var scope = sp.CreateAsyncScope();
    var row = await scope.ServiceProvider.GetRequiredService<IPerspectiveStore<RebuildBalanceModel>>()
      .GetByStreamIdAsync(stream, CancellationToken.None);
    return row?.Balance;
  }

  private async Task<Guid?> _cursorAsync(Guid stream) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = $2", conn);
    cmd.Parameters.AddWithValue(stream);
    cmd.Parameters.AddWithValue(RebuildBalancePerspectiveName);
    return await cmd.ExecuteScalarAsync() as Guid?;
  }

  // ===== #1956: a replay that starts after a stream's first event folds the collectives committed after its start =====

  [Test]
  public async Task Rewind_WithoutASnapshot_FoldsTheCollectives_AndCheckpointsOnTheRowsOwnEventAsync() {
    await using var sp = _buildServices();
    var streamA = Guid.NewGuid();
    Guid lastCredit;
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 100m });
      await _appendCollectiveAsync(eventStore, _reset(streamA, 0m));
      lastCredit = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 5m });
    }

    var completion = await _rewindAsync(sp, streamA, lastCredit);

    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(5m)
      .Because("the reset committed between the credits folds where it was committed, as a full rebuild folds it");
    await Assert.That(completion.LastEventId).IsEqualTo(lastCredit)
      .Because("a collective folds into the row without moving its checkpoint");
  }

  [Test]
  public async Task Rewind_FromASnapshot_FoldsTheCollectivesCommittedAfterItAsync() {
    await using var sp = _buildServices(withSnapshots: true);
    var streamA = Guid.NewGuid();
    Guid atSnapshot;
    Guid trigger;
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      atSnapshot = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 100m });
      trigger = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 7m });
      await _appendCollectiveAsync(eventStore, _reset(streamA, 1m));
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 2m });
    }
    // The snapshot says 1000 where the events say 100, so the row shows the rewind started from it.
    await _snapshotAsync(streamA, atSnapshot, new RebuildBalanceModel { Id = streamA, Balance = 1000m });

    _ = await _rewindAsync(sp, streamA, trigger);

    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(3m)
      .Because("the rewind restores 1000, folds the credit of 7, then the reset to 1 committed after the snapshot, then the credit of 2");
  }

  // The snapshot holds every collective committed before the event it was taken at: folding one of those again would
  // apply it twice.
  [Test]
  public async Task Rewind_FromASnapshot_DoesNotFoldAgainACollectiveCommittedBeforeItAsync() {
    await using var sp = _buildServices(withSnapshots: true);
    var streamA = Guid.NewGuid();
    Guid atSnapshot;
    Guid trigger;
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 100m });
      await _appendCollectiveAsync(eventStore, _reset(streamA, 0m));
      atSnapshot = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 5m });
      trigger = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 7m });
    }
    await _snapshotAsync(streamA, atSnapshot, new RebuildBalanceModel { Id = streamA, Balance = 1000m });

    _ = await _rewindAsync(sp, streamA, trigger);

    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(1007m)
      .Because("the reset was committed before the snapshot's event, so the snapshot already holds it");
  }

  // The issue's case: a default rebuild of a stream archived through a prefix starts from its newest live snapshot.
  [Test]
  public async Task Rebuild_ByDefault_FromTheNewestLiveSnapshot_FoldsACollectiveCommittedAfterItAsync() {
    await using var sp = _buildServices(withArchive: true, withSnapshots: true);
    var streamA = Guid.NewGuid();
    Guid atSnapshot;
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 10m });
      atSnapshot = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 30m });
      await _appendCollectiveAsync(eventStore, _reset(streamA, 0m));
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 2m });
    }
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(streamA, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await _snapshotAsync(streamA, atSnapshot, new RebuildBalanceModel { Id = streamA, Balance = 1000m });

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(
      RebuildBalancePerspectiveName, [streamA], CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");
    await Assert.That(result.StreamsSkipped).IsEqualTo(0).Because("the control: the stream started from its snapshot");
    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(2m)
      .Because("the reset committed after the snapshot folds before the credit of 2 committed after it");
  }

  [Test]
  public async Task Rebuild_ByDefault_FromASnapshotAtTheStreamsLastEvent_StillFoldsTheCollectivesCommittedAfterItAsync() {
    await using var sp = _buildServices(withArchive: true, withSnapshots: true);
    var streamA = Guid.NewGuid();
    Guid atSnapshot;
    await using (var scope = sp.CreateAsyncScope()) {
      var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await _appendAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 10m });
      atSnapshot = await _appendMintedAsync(eventStore, streamA, new RebuildCreditedEvent { StreamId = streamA, Amount = 30m });
    }
    // The row the live path keeps, before anything is archived.
    var first = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(
      RebuildBalancePerspectiveName, [streamA], CancellationToken.None);
    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(40m).Because(first.Error ?? "the control: the row exists");
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(streamA, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await _snapshotAsync(streamA, atSnapshot, new RebuildBalanceModel { Id = streamA, Balance = 1000m });
    await using (var scope = sp.CreateAsyncScope()) {
      await _appendCollectiveAsync(scope.ServiceProvider.GetRequiredService<IEventStore>(), _reset(streamA, 0m));
    }

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(
      RebuildBalancePerspectiveName, [streamA], CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");
    await Assert.That(await _balanceAsync(sp, streamA)).IsEqualTo(0m)
      .Because("no event of the stream follows the snapshot, but the reset committed after it still folds into the row");
    await Assert.That(await _cursorAsync(streamA)).IsEqualTo(atSnapshot)
      .Because("the collective moves no checkpoint, so the row stays on the snapshot's event");
  }

  // Appends with an id minted at append time, so the stream's ids run in the order its events were appended.
  private static async Task<Guid> _appendMintedAsync(IEventStore eventStore, Guid streamId, RebuildCreditedEvent payload) {
    var id = Guid.CreateVersion7();
    await _appendAsync(eventStore, streamId, payload, id);
    return id;
  }

  // A collective is its own single-event stream.
  private static Task _appendCollectiveAsync(IEventStore eventStore, RebuildResetBalanceCollectiveEvent collective) =>
    _appendAsync(eventStore, collective.StreamId, collective);

  private static RebuildResetBalanceCollectiveEvent _reset(Guid target, decimal to) => new() {
    StreamId = Guid.NewGuid(),
    Scope = new TenantCollectiveScope(string.Empty),
    TargetId = target,
    ResetTo = to,
  };

  private static async Task<PerspectiveCursorCompletion> _rewindAsync(ServiceProvider sp, Guid stream, Guid trigger) {
    await using var scope = sp.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetRequiredService<IPerspectiveRunnerRegistry>()
      .GetRunner(RebuildBalancePerspectiveName, scope.ServiceProvider)!;
    return await runner.RewindAndRunAsync(stream, RebuildBalancePerspectiveName, trigger, CancellationToken.None);
  }

  private async Task _snapshotAsync(Guid stream, Guid atEvent, RebuildBalanceModel model) {
    var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RebuildBalanceModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RebuildBalanceModel));
    // Stamped with the serialization version as the runner writes a snapshot, so the runner reads it back.
    using var modelData = System.Text.Json.JsonSerializer.SerializeToDocument(model, typeInfo);
    using var data = SnapshotEnvelope.Wrap(modelData.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
    await new EFCorePerspectiveSnapshotStore(NpgsqlDataSource.Create(ConnectionString))
      .CreateSnapshotAsync(stream, RebuildBalancePerspectiveName, atEvent, data);
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
