using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #983: the runner strips a Split class model's promoted fields in place before the write, so the
/// document never holds them. It snapshotted the same instance after the write, so the snapshot lacked
/// them too, and a rewind that started from it replayed onto their defaults and wrote those over the
/// columns. The snapshot is now taken from the model as it was applied.
/// </summary>
/// <remarks>Driven through the generated runner and the Postgres snapshot store, so the write, the snapshot and the rewind are the ones an application runs.</remarks>
[Category("Shard2")]
public class SplitClassSnapshotRewindTests : EFCoreTestBase {
  private const string TABLE_NAME = "wh_per_split_class_snapshot";

  private static async Task<Guid> _appendAsync<TEvent>(InMemoryEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    var messageId = MessageId.New();
    await eventStore.AppendAsync(streamId, new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    });
    return messageId.Value;
  }

  private static SplitClassSnapshotPerspectiveRunner _runner(
      InMemoryEventStore eventStore, WorkCoordinationDbContext context, IPerspectiveSnapshotStore snapshotStore, int everyNEvents = 1) {
    var services = new ServiceCollection();
    services.AddTransient<SplitClassSnapshotPerspective>();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    return new SplitClassSnapshotPerspectiveRunner(
        provider,
        provider.GetRequiredService<ILogger<SplitClassSnapshotPerspectiveRunner>>(),
        eventStore,
        new EFCorePostgresPerspectiveStore<SplitClassSnapshotModel>(context, TABLE_NAME),
        provider.GetRequiredService<IServiceScopeFactory>(),
        snapshotStore: snapshotStore,
        snapshotOptions: Microsoft.Extensions.Options.Options.Create(new PerspectiveSnapshotOptions {
          Enabled = true,
          SnapshotEveryNEvents = everyNEvents,
          MaxSnapshotsPerStream = 10
        }));
  }

  private sealed record StoredColumns(string? Status, int Priority, string? Note, string? DocumentStatus);

  private async Task<StoredColumns> _readColumnsAsync(Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        $"SELECT status, priority, data ->> 'Note', data ->> 'Status' FROM {TABLE_NAME} WHERE id = @id", connection);
    command.Parameters.AddWithValue("id", streamId);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new StoredColumns(
        await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
        reader.GetInt32(1),
        await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
        await reader.IsDBNullAsync(3) ? null : reader.GetString(3));
  }

  [Test]
  public async Task Rewind_FromASnapshotOfASplitClassModel_KeepsThePromotedColumnsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var snapshotStore = new EFCorePerspectiveSnapshotStore(dataSource);
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();

    // The first run applies the promoted fields and snapshots the model it applied.
    await _appendAsync(eventStore, streamId, new SplitClassSnapshotStatusSetEvent { StreamId = streamId, Status = "active", Priority = 7 });
    await using (var context = CreateDbContext()) {
      var first = await _runner(eventStore, context, snapshotStore).RunAsync(streamId, TABLE_NAME, null, CancellationToken.None);
      await Assert.That(first.EventsProcessed).IsEqualTo(1);
    }

    var snapshot = await snapshotStore.GetLatestSnapshotAsync(streamId, TABLE_NAME);
    await Assert.That(snapshot).IsNotNull();
    await Assert.That(snapshot!.Value.SnapshotData.RootElement.GetRawText()).Contains("active")
      .Because("the snapshot is the model as it was applied, not the copy the write stripped for the document");

    // A later event that leaves the promoted fields alone, then a rewind that replays it from that snapshot.
    var noteEventId = await _appendAsync(eventStore, streamId, new SplitClassSnapshotNoteChangedEvent { StreamId = streamId, Note = "second" });
    await using (var context = CreateDbContext()) {
      await _runner(eventStore, context, snapshotStore).RunAsync(streamId, TABLE_NAME, null, CancellationToken.None);
    }
    await using (var context = CreateDbContext()) {
      var rewound = await _runner(eventStore, context, snapshotStore).RewindAndRunAsync(streamId, TABLE_NAME, noteEventId);
      await Assert.That(rewound.EventsProcessed).IsEqualTo(1)
        .Because("the rewind restores the snapshot before the note event and replays only that event");
    }

    var stored = await _readColumnsAsync(streamId);
    await Assert.That(stored.Note).IsEqualTo("second");
    await Assert.That(stored.Status).IsEqualTo("active")
      .Because("a rewind from the snapshot must not write a promoted field's default over its column");
    await Assert.That(stored.Priority).IsEqualTo(7);
    await Assert.That(stored.DocumentStatus).IsNull()
      .Because("the document of a Split model still does not hold its promoted fields");
  }

  [Test]
  public async Task ASplitClassModel_IsSnapshottedOnlyOnARunThatReachesTheCadence_WithItsPromotedFieldsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var snapshotStore = new EFCorePerspectiveSnapshotStore(dataSource);
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await using var context = CreateDbContext();
    // One runner across both runs, as a host keeps it: the count of events since the last snapshot is its own.
    var runner = _runner(eventStore, context, snapshotStore, everyNEvents: 2);

    await _appendAsync(eventStore, streamId, new SplitClassSnapshotStatusSetEvent { StreamId = streamId, Status = "active", Priority = 7 });
    await runner.RunAsync(streamId, TABLE_NAME, null, CancellationToken.None);
    await Assert.That(await snapshotStore.GetLatestSnapshotAsync(streamId, TABLE_NAME)).IsNull()
      .Because("issue #1002: one event of a cadence of two is not due, decided before the write");

    await _appendAsync(eventStore, streamId, new SplitClassSnapshotNoteChangedEvent { StreamId = streamId, Note = "second" });
    await runner.RunAsync(streamId, TABLE_NAME, null, CancellationToken.None);
    var snapshot = await snapshotStore.GetLatestSnapshotAsync(streamId, TABLE_NAME);
    await Assert.That(snapshot).IsNotNull().Because("the second event reaches the cadence");
    await Assert.That(snapshot!.Value.SnapshotData.RootElement.GetRawText()).Contains("active")
      .Because("the due run serializes the model before the write strips it in place");
  }
}
