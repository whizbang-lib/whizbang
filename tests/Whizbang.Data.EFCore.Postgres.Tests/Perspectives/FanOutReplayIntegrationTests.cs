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
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// #2621: a rebuild or rewind of a row a fan-out targets folds the foreign events of the streams its key held, merged with
/// the row's own events in commit order, and comes out as the live row did. Live here is what the drain and the collective
/// sink do: each of the order's own events applied by its runner as it is committed, and each rename applied by the
/// collective dispatcher's set-based update as it is committed. Then the rows are rebuilt and compared.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveReplayApplier.cs</tests>
/// <tests>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</tests>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard3")]
public class FanOutReplayIntegrationTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.FanOutReplayOrderPerspective";
  private const string TENANT = "tenant-fan-out-replay";

  private static readonly IReadOnlyList<Type> _orderEvents =
    [typeof(FanOutReplayOrderPlaced), typeof(FanOutReplayOrderMoved), typeof(FanOutReplayOrderShipped)];

  // The last event each order's live runner applied, as its cursor would hold it.
  private readonly Dictionary<Guid, Guid?> _liveCursor = [];

  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private sealed class Listed(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

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
    if (withSnapshots) {
      services.AddSingleton<IPerspectiveSnapshotStore>(new EFCorePerspectiveSnapshotStore(NpgsqlDataSource.Create(ConnectionString)));
    }
    services.AddScoped<IEventStoreQuery>(sp => new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IPerspectiveStore<FanOutReplayOrder>>(sp =>
      new EFCorePostgresPerspectiveStore<FanOutReplayOrder>(sp.GetRequiredService<WorkCoordinationDbContext>(), FanOutReplayOrderFanOut.TABLE));
    services.AddScoped<FanOutReplayOrderPerspective>();
    services.AddSingleton<FanOutReplayOrderFanOut>();

    var asm = typeof(FanOutReplayIntegrationTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "FanOutReplayOrderPerspectiveRunner"));
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry),
      asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" && t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated"));
    services.AddScoped<IReceptorInvoker>(_ => new NoOpReceptorInvoker());
    services.AddScoped<IPerspectiveCheckpointCompleter>(sp =>
      new EFCorePostgresPerspectiveCheckpointCompleter(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();

    // The fan-out as the generated registry serves it, and the collective registration the driver makes for a host that
    // declares one: the dispatcher (live) and the replay applier (rebuild and rewind), with no executor registered by hand.
    services.AddSingleton<IPerspectiveFanOutRegistry>(new Listed([FanOutReplayOrderFanOut.Renamed]));
    services.AddCollectiveEventsEFCore<WorkCoordinationDbContext>();
    return services.BuildServiceProvider();
  }

  // ===== live equals rebuild =====

  [Test]
  public async Task Rebuild_ARenameCommittedAfterOrderPlaced_FoldsIntoTheOrder_AsLiveAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    await _renameAsync(sp, customer, "Acme Ltd");

    await _assertRebuildIsLiveAsync(sp, [order], expectedName: "Acme Ltd");
  }

  [Test]
  public async Task Rebuild_ARenameCommittedBeforeOrderPlaced_AppliesToNoRow_AsLiveAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _renameAsync(sp, customer, "Acme Ltd");
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));

    await _assertRebuildIsLiveAsync(sp, [order], expectedName: "Acme",
      because: "the rename was committed before the order took the customer, so live it reached no row of it");
  }

  [Test]
  public async Task Rebuild_AnOrderMovedToAnotherCustomer_FoldsEachCustomersRenamesOnlyWhileTheOrderIsTheirs_AsLiveAsync() {
    await using var sp = _buildServices();
    var (first, second, order) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, first, "First"));
    await _renameAsync(sp, first, "First renamed");
    await _ownAsync(sp, order, new FanOutReplayOrderMoved { OrderId = order, CustomerId = second, CustomerName = "Second" });
    await _renameAsync(sp, second, "Second renamed");
    await _renameAsync(sp, first, "First renamed again");

    await _assertRebuildIsLiveAsync(sp, [order], expectedName: "Second renamed",
      because: "after the move only the second customer's renames reach the order, though the first's is committed last");
  }

  [Test]
  public async Task Rebuild_TwoRenamesOfOneCustomer_TheLastWins_OnEveryOrderOfIt_AsLiveAsync() {
    await using var sp = _buildServices();
    var (customer, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var (o1, o2, o3) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, o1, _placed(o1, customer, "Acme"));
    await _ownAsync(sp, o3, _placed(o3, other, "Brightway"));
    await _renameAsync(sp, customer, "Acme 2");
    await _ownAsync(sp, o2, _placed(o2, customer, "Acme 2"));
    await _renameAsync(sp, customer, "Acme 3");
    await _ownAsync(sp, o1, new FanOutReplayOrderShipped { OrderId = o1, Status = "Shipped" });

    var live = await _rowsAsync(sp, [o1, o2, o3]);
    await Assert.That(live[o1].Name).IsEqualTo("Acme 3").Because("the control: live, the last rename reached o-1");
    await Assert.That(live[o2].Name).IsEqualTo("Acme 3");
    await Assert.That(live[o3].Name).IsEqualTo("Brightway").Because("another customer's order is never targeted");
    await _rebuildAsync(sp, [o1, o2, o3]);
    await Assert.That(await _rowsAsync(sp, [o1, o2, o3])).IsEquivalentTo(live);
  }

  // The decision (#2275): received events follow the order a replay gives their own stream (ReplayEventOrder). A rename
  // received after a later one of the same producer is stored behind it, and folds in the producer's order.
  [Test]
  public async Task Rebuild_ARenameReceivedOutOfItsProducersOrder_FoldsInTheProducersOrderAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    var producer = Guid.CreateVersion7();
    var second = await _appendRenameAsync(sp, customer, "Acme 2");
    var first = await _appendRenameAsync(sp, customer, "Acme 1");
    await _executeAsync($"UPDATE wh_event_store SET origin_service_id = '{producer}', origin_commit_sequence = 2 WHERE event_id = '{second}'");
    await _executeAsync($"UPDATE wh_event_store SET origin_service_id = '{producer}', origin_commit_sequence = 1 WHERE event_id = '{first}'");

    await _rebuildAsync(sp, [order]);

    await Assert.That((await _rowsAsync(sp, [order]))[order].Name).IsEqualTo("Acme 2")
      .Because("the producer renamed to Acme 1 and then to Acme 2, whatever order this service stored them in");
  }

  // ===== archived foreign streams =====

  [Test]
  public async Task Rebuild_IncludeAll_FoldsTheRenamesOfACustomerStreamArchivedWholeAsync() {
    await using var sp = _buildServices(withArchive: true);
    var (customer, order) = await _orderOfAnArchivedCustomerAsync(sp);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(
      PERSPECTIVE, [order], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll }, CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");
    await Assert.That((await _rowsAsync(sp, [order]))[order].Name).IsEqualTo("Acme Ltd")
      .Because($"customer {customer}'s stream is read through the archive, as the rebuild reads any stream");
  }

  [Test]
  public async Task Rebuild_ByDefault_LeavesOutTheRenamesOfACustomerStreamArchivedWholeAsync() {
    await using var sp = _buildServices(withArchive: true);
    var (_, order) = await _orderOfAnArchivedCustomerAsync(sp);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(PERSPECTIVE, [order], CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");
    await Assert.That((await _rowsAsync(sp, [order]))[order].Name).IsEqualTo("Acme")
      .Because("a rebuild reads no archived data by default, and the customer's stream is archived whole");
  }

  private async Task<(Guid Customer, Guid Order)> _orderOfAnArchivedCustomerAsync(ServiceProvider sp) {
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    await _renameAsync(sp, customer, "Acme Ltd");
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(customer))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _scalarAsync<long>($"SELECT count(*) FROM wh_event_store WHERE stream_id = '{customer}'")).IsEqualTo(0L)
      .Because("the control: the customer's stream is archived whole, so the hot store holds none of it");
    return (customer, order);
  }

  // ===== rewind from a snapshot (#1956) =====

  [Test]
  public async Task Rewind_FromASnapshot_DoesNotFoldARenameCommittedBeforeItsEventAsync() {
    await using var sp = _buildServices(withSnapshots: true);
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    await _renameAsync(sp, customer, "Acme Ltd");
    var atSnapshot = await _ownAsync(sp, order, new FanOutReplayOrderShipped { OrderId = order, Status = "Packed" });
    var trigger = await _ownAsync(sp, order, new FanOutReplayOrderShipped { OrderId = order, Status = "Shipped" });
    // The snapshot says "Snapshot" where the events say Acme Ltd, so the row shows whether the rename folded again.
    await _snapshotAsync(order, atSnapshot, new FanOutReplayOrder { Id = order, CustomerId = customer, CustomerName = "Snapshot", Status = "Packed" });

    await _rewindAsync(sp, order, trigger);

    var row = (await _rowsAsync(sp, [order]))[order];
    await Assert.That(row.Status).IsEqualTo("Shipped").Because("the control: the rewind folded the trigger onto the snapshot");
    await Assert.That(row.Name).IsEqualTo("Snapshot").Because("the snapshot already holds the rename committed before its event");
  }

  [Test]
  public async Task Rewind_FromASnapshot_FoldsARenameCommittedAfterIt_ForTheKeyTheSnapshotHoldsAsync() {
    await using var sp = _buildServices(withSnapshots: true);
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    var atSnapshot = await _ownAsync(sp, order, new FanOutReplayOrderShipped { OrderId = order, Status = "Packed" });
    var trigger = await _ownAsync(sp, order, new FanOutReplayOrderShipped { OrderId = order, Status = "Shipped" });
    await _renameAsync(sp, customer, "Acme Ltd");
    await _snapshotAsync(order, atSnapshot, new FanOutReplayOrder { Id = order, CustomerId = customer, CustomerName = "Snapshot", Status = "Packed" });

    await _rewindAsync(sp, order, trigger);

    var row = (await _rowsAsync(sp, [order]))[order];
    await Assert.That(row.Status).IsEqualTo("Shipped");
    await Assert.That(row.Name).IsEqualTo("Acme Ltd")
      .Because("the snapshot's row holds the customer, and the rename was committed after the snapshot's event");
  }

  [Test]
  public async Task Rewind_FromTheStreamsStart_FoldsTheRenamesWhereTheyWereCommitted_AsLiveAsync() {
    await using var sp = _buildServices();
    var (first, second, order) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var placed = await _ownAsync(sp, order, _placed(order, first, "First"));
    await _renameAsync(sp, first, "First renamed");
    await _ownAsync(sp, order, new FanOutReplayOrderMoved { OrderId = order, CustomerId = second, CustomerName = "Second" });
    await _renameAsync(sp, first, "First renamed again");
    var live = await _rowsAsync(sp, [order]);

    await _rewindAsync(sp, order, placed);

    await Assert.That(live[order].Name).IsEqualTo("Second").Because("the control: live, the move's name held");
    await Assert.That(await _rowsAsync(sp, [order])).IsEquivalentTo(live);
  }

  // ===== helpers =====

  private static FanOutReplayOrderPlaced _placed(Guid order, Guid customer, string name) =>
    new() { OrderId = order, CustomerId = customer, CustomerName = name };

  private static async Task _assertRebuildIsLiveAsync(ServiceProvider sp, Guid[] orders, string expectedName, string? because = null) {
    var live = await _rowsAsync(sp, orders);
    await Assert.That(live[orders[0]].Name).IsEqualTo(expectedName).Because(because ?? "the control: the live row");
    await _rebuildAsync(sp, orders);
    await Assert.That(await _rowsAsync(sp, orders)).IsEquivalentTo(live).Because("a rebuild comes out as the live rows did");
  }

  // One of the order's own events, committed and then applied by its runner, as the drain applies it.
  private async Task<Guid> _ownAsync<TEvent>(ServiceProvider sp, Guid order, TEvent evt) where TEvent : IEvent {
    var id = await _appendAsync(sp, order, evt);
    await using var scope = sp.CreateAsyncScope();
    var after = _liveCursor.GetValueOrDefault(order);
    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var envelope in scope.ServiceProvider.GetRequiredService<IEventStore>().ReadPolymorphicAsync(order, after, _orderEvents)) {
      events.Add(envelope);
    }
    var runner = scope.ServiceProvider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRunner(PERSPECTIVE, scope.ServiceProvider)!;
    _ = await runner.RunWithEventsAsync(order, PERSPECTIVE, after, events, CancellationToken.None);
    _liveCursor[order] = id;
    return id;
  }

  // A rename of the customer, committed and then applied by the collective sink's set-based update.
  private async Task _renameAsync(ServiceProvider sp, Guid customer, string name) {
    var rename = new FanOutReplayCustomerRenamed { CustomerId = customer, Name = name };
    var id = await _appendAsync(sp, customer, rename);
    await using var scope = sp.CreateAsyncScope();
    var dispatcher = scope.ServiceProvider.GetRequiredService<ICollectiveDispatcher>();
    _ = await dispatcher.DispatchAsync(
      PerspectiveFanOutEntry.CollectiveEventFor(rename, customer, TENANT), id, scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>());
  }

  private Task<Guid> _appendRenameAsync(ServiceProvider sp, Guid customer, string name) =>
    _appendAsync(sp, customer, new FanOutReplayCustomerRenamed { CustomerId = customer, Name = name });

  // Appended as its own type, as a producer appends it, so the store records the event's type.
  private async Task<Guid> _appendAsync<TEvent>(ServiceProvider sp, Guid stream, TEvent payload) where TEvent : IEvent {
    var id = Guid.CreateVersion7();
    await using (var scope = sp.CreateAsyncScope()) {
      await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(stream, new MessageEnvelope<TEvent> {
        MessageId = new MessageId(id),
        Payload = payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UnixEpoch,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = TENANT, UserId = "user-fan-out-replay" }),
        }],
      });
    }
    // Committed: the event takes its place in the store's commit order, as the live paths see it.
    await _executeAsync("SELECT stamp_pending_commit_sequences()");
    return id;
  }

  private static async Task _rebuildAsync(ServiceProvider sp, Guid[] orders) {
    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(PERSPECTIVE, orders, CancellationToken.None);
    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "rebuild should succeed");
  }

  private static async Task _rewindAsync(ServiceProvider sp, Guid order, Guid trigger) {
    await using var scope = sp.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRunner(PERSPECTIVE, scope.ServiceProvider)!;
    _ = await runner.RewindAndRunAsync(order, PERSPECTIVE, trigger, CancellationToken.None);
  }

  private static async Task<Dictionary<Guid, (Guid Customer, string Name, string Status)>> _rowsAsync(ServiceProvider sp, Guid[] orders) {
    await using var scope = sp.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IPerspectiveStore<FanOutReplayOrder>>();
    var rows = new Dictionary<Guid, (Guid, string, string)>();
    foreach (var order in orders) {
      var row = await store.GetByStreamIdAsync(order, CancellationToken.None);
      rows[order] = (row!.CustomerId, row.CustomerName, row.Status);
    }
    return rows;
  }

  private async Task _snapshotAsync(Guid stream, Guid atEvent, FanOutReplayOrder model) {
    var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<FanOutReplayOrder>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(FanOutReplayOrder));
    using var modelData = System.Text.Json.JsonSerializer.SerializeToDocument(model, typeInfo);
    using var data = SnapshotEnvelope.Wrap(modelData.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
    await new EFCorePerspectiveSnapshotStore(NpgsqlDataSource.Create(ConnectionString))
      .CreateSnapshotAsync(stream, PERSPECTIVE, atEvent, null, data, null);
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
  }
}
