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
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A Split perspective keeps its promoted fields only in their columns. The model a later event is
/// applied to has to be read back with those values, because the runner writes every promoted column
/// from the model it applied: a model loaded without them would write their defaults over the stored
/// values, silently, on every event that does not set them.
/// </summary>
/// <remarks>Driven through the generated runner, so the load, the apply and the write are the ones an application runs.</remarks>
[Category("Shard2")]
public class SplitPhysicalFieldReloadTests : EFCoreTestBase {
  private const string TABLE_NAME = "wh_per_split_reload";

  private static SplitReloadPerspectiveRunner _runner(InMemoryEventStore eventStore, WorkCoordinationDbContext context) {
    var services = new ServiceCollection();
    services.AddTransient<SplitReloadPerspective>();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    return new SplitReloadPerspectiveRunner(
        provider,
        provider.GetRequiredService<ILogger<SplitReloadPerspectiveRunner>>(),
        eventStore,
        new EFCorePostgresPerspectiveStore<SplitReloadModel>(context, TABLE_NAME),
        provider.GetRequiredService<IServiceScopeFactory>());
  }

  private static async Task _appendAsync<TEvent>(InMemoryEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    await eventStore.AppendAsync(streamId, new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    });
  }

  private sealed record StoredColumns(string? Status, int Priority, string? Note);

  private async Task<StoredColumns> _readColumnsAsync(Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        $"SELECT status, priority, data ->> 'Note' FROM {TABLE_NAME} WHERE id = @id", connection);
    command.Parameters.AddWithValue("id", streamId);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new StoredColumns(
        await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
        reader.GetInt32(1),
        await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
  }

  private async Task _runAsync(InMemoryEventStore eventStore, Guid streamId, Guid? lastProcessedEventId, int expectedEvents) {
    await using var context = CreateDbContext();
    var result = await _runner(eventStore, context).RunAsync(streamId, TABLE_NAME, lastProcessedEventId, CancellationToken.None);
    await Assert.That(result.EventsProcessed).IsEqualTo(expectedEvents);
  }

  [Test]
  public async Task Apply_AnEventThatLeavesThePromotedFieldsAlone_KeepsTheirColumnsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, streamId, new SplitReloadStatusSetEvent { StreamId = streamId, Status = "active", Priority = 7 });
    await _runAsync(eventStore, streamId, null, expectedEvents: 1);

    var afterFirst = await _readColumnsAsync(streamId);
    await Assert.That(afterFirst.Status).IsEqualTo("active");
    await Assert.That(afterFirst.Priority).IsEqualTo(7);

    // A later run, on a fresh context, applies an event that only changes the document field.
    await _appendAsync(eventStore, streamId, new SplitReloadNoteChangedEvent { StreamId = streamId, Note = "second" });
    await _runAsync(eventStore, streamId, null, expectedEvents: 1);

    var afterSecond = await _readColumnsAsync(streamId);
    await Assert.That(afterSecond.Note).IsEqualTo("second")
      .Because("the second event was applied and written");
    await Assert.That(afterSecond.Status).IsEqualTo("active")
      .Because("an event that does not touch a promoted field must not overwrite its column with a default");
    await Assert.That(afterSecond.Priority).IsEqualTo(7)
      .Because("an event that does not touch a promoted field must not overwrite its column with a default");
  }

  [Test]
  public async Task GetByStreamIdAsync_ReturnsThePromotedFieldsFromTheirColumnsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, streamId, new SplitReloadStatusSetEvent { StreamId = streamId, Status = "queued", Priority = 3 });
    await _appendAsync(eventStore, streamId, new SplitReloadNoteChangedEvent { StreamId = streamId, Note = "first" });
    await _runAsync(eventStore, streamId, null, expectedEvents: 2);

    await using var context = CreateDbContext();
    var model = await new EFCorePostgresPerspectiveStore<SplitReloadModel>(context, TABLE_NAME).GetByStreamIdAsync(streamId);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Status).IsEqualTo("queued")
      .Because("a Split field lives only in its column, so the load must read it from there");
    await Assert.That(model.Priority).IsEqualTo(3);
    await Assert.That(model.Note).IsEqualTo("first");
    await Assert.That(context.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("the load leaves nothing tracked for the write that follows it");
  }
  private async Task<Guid> _seedAsync(string status, int priority) {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, streamId, new SplitReloadStatusSetEvent { StreamId = streamId, Status = status, Priority = priority });
    await _runAsync(eventStore, streamId, null, expectedEvents: 1);
    return streamId;
  }

  [Test]
  public async Task GetByStreamIdAsync_AMissingRow_IsNullAsync() {
    await using var context = CreateDbContext();

    var model = await new EFCorePostgresPerspectiveStore<SplitReloadModel>(context, TABLE_NAME).GetByStreamIdAsync(Guid.CreateVersion7());

    await Assert.That(model).IsNull();
  }

  [Test]
  public async Task GetByStreamIdAsync_ARowTheContextHoldsUnchanged_IsReadAfreshAsync() {
    var streamId = await _seedAsync("held", 5);
    await using var context = CreateDbContext();
    // A plain tracked read holds the row as its document has it, without the promoted fields.
    var held = await context.Set<PerspectiveRow<SplitReloadModel>>().SingleAsync(r => r.Id == streamId);
    await Assert.That(held.Data.Status).IsNotEqualTo("held");

    var model = await new EFCorePostgresPerspectiveStore<SplitReloadModel>(context, TABLE_NAME).GetByStreamIdAsync(streamId);

    await Assert.That(model!.Status).IsEqualTo("held")
      .Because("the held instance is let go, so the read materializes the row and its columns");
    await Assert.That(model.Priority).IsEqualTo(5);
    await Assert.That(context.ChangeTracker.Entries().Count()).IsEqualTo(0);
  }

  [Test]
  public async Task GetByStreamIdAsync_ARowHeldWithAPendingChange_StaysTrackedForItsWriteAsync() {
    var streamId = await _seedAsync("pending", 2);
    await using var context = CreateDbContext();
    var held = await context.Set<PerspectiveRow<SplitReloadModel>>().SingleAsync(r => r.Id == streamId);
    context.Entry(held).State = EntityState.Modified;

    var model = await new EFCorePostgresPerspectiveStore<SplitReloadModel>(context, TABLE_NAME).GetByStreamIdAsync(streamId);

    await Assert.That(model).IsSameReferenceAs(held.Data)
      .Because("a row with a pending change is returned as the context holds it");
    await Assert.That(context.Entry(held).State).IsEqualTo(EntityState.Modified)
      .Because("the load must not drop a write the context has yet to save");
  }

  [Test]
  public async Task GetByStreamIdAsync_OnAContextThatHydratesAsItTracks_ReturnsThatHydrationAsync() {
    var streamId = await _seedAsync("lens", 9);
    // A context a lens has read through carries a hydrator that copies the columns and detaches the row as
    // it is tracked, before the store sees it. This registers the one the generated code does for the model.
    SplitModeChangeTrackerHydrator.Register(typeof(PerspectiveRow<SplitReloadModel>), entry => {
      var row = (PerspectiveRow<SplitReloadModel>)entry.Entity;
      row.Data.Status = (string?)entry.Property("status").CurrentValue;
      row.Data.Priority = (int)entry.Property("priority").CurrentValue!;
      entry.State = EntityState.Detached;
    });
    await using var context = CreateDbContext();
    SplitModeChangeTrackerHydrator.EnsureHooked(context);

    var model = await new EFCorePostgresPerspectiveStore<SplitReloadModel>(context, TABLE_NAME).GetByStreamIdAsync(streamId);

    await Assert.That(model!.Status).IsEqualTo("lens");
    await Assert.That(model.Priority).IsEqualTo(9);
    await Assert.That(context.ChangeTracker.Entries().Count()).IsEqualTo(0);
  }
}
