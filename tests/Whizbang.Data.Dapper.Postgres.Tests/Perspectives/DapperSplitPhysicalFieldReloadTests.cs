using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres.Tests.Generated;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// A Split perspective keeps its promoted fields only in their columns. The model a later event is
/// applied to has to be read back with those values, because the runner writes every promoted column
/// from the model it applied: a model loaded without them would write their defaults over the stored
/// values, silently, on every event that does not set them.
/// </summary>
/// <remarks>Driven through the generated runner, so the load, the apply and the write are the ones an application runs.</remarks>
[NotInParallel("PostgreSQL")]
public class DapperSplitPhysicalFieldReloadTests : PostgresTestBase {
  private const string TABLE_NAME = "wh_per_dapper_split_reload";
  private static readonly float[] _components = [1f, 2f, 3f];
  private static readonly DateTimeOffset _placedAt = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
      DapperSplitReloadJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
  };

  [Before(Test)]
  public async Task CreateTableAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"""
      CREATE TABLE {TABLE_NAME} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL,
        status TEXT, priority INTEGER, tier INTEGER, placed_at TIMESTAMPTZ);
      """, connection);
    await command.ExecuteNonQueryAsync();
  }

  private DapperPostgresPerspectiveStore<DapperSplitReloadModel> _store() => new(ConnectionString, TABLE_NAME, _jsonOptions);

  private DapperSplitReloadPerspectiveRunner _runner(InMemoryEventStore eventStore) {
    var services = new ServiceCollection();
    services.AddTransient<DapperSplitReloadPerspective>();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    return new DapperSplitReloadPerspectiveRunner(
        provider,
        provider.GetRequiredService<ILogger<DapperSplitReloadPerspectiveRunner>>(),
        eventStore,
        _store(),
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

  private sealed record StoredColumns(string? Status, int? Priority, int? Tier, DateTimeOffset? PlacedAt, string? Note);

  private async Task<StoredColumns> _readColumnsAsync(Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        $"SELECT status, priority, tier, placed_at, data ->> 'Note' FROM {TABLE_NAME} WHERE id = @id", connection);
    command.Parameters.AddWithValue("id", streamId);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    async Task<T?> field<T>(int ordinal) => await reader.IsDBNullAsync(ordinal) ? default : await reader.GetFieldValueAsync<T>(ordinal);
    return new StoredColumns(await field<string>(0), await field<int?>(1), await field<int?>(2), await field<DateTimeOffset?>(3), await field<string>(4));
  }

  private static DapperSplitReloadStatusSetEvent _statusSet(Guid streamId, string status, int priority) => new() {
    StreamId = streamId,
    Status = status,
    Priority = priority,
    Tier = DapperSplitReloadTier.Gold,
    PlacedAt = _placedAt,
  };

  [Test]
  public async Task Apply_AnEventThatLeavesThePromotedFieldsAlone_KeepsTheirColumnsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, streamId, _statusSet(streamId, "active", 7));
    var first = await _runner(eventStore).RunAsync(streamId, TABLE_NAME, null, CancellationToken.None);
    await Assert.That(first.EventsProcessed).IsEqualTo(1);

    var afterFirst = await _readColumnsAsync(streamId);
    await Assert.That(afterFirst.Status).IsEqualTo("active");
    await Assert.That(afterFirst.Priority).IsEqualTo(7);

    // A later run applies only an event that changes the document field.
    await _appendAsync(eventStore, streamId, new DapperSplitReloadNoteChangedEvent { StreamId = streamId, Note = "second" });
    var second = await _runner(eventStore).RunAsync(streamId, TABLE_NAME, first.LastEventId, CancellationToken.None);
    await Assert.That(second.EventsProcessed).IsEqualTo(1);

    var afterSecond = await _readColumnsAsync(streamId);
    await Assert.That(afterSecond.Note).IsEqualTo("second")
      .Because("the second event was applied and written");
    await Assert.That(afterSecond.Status).IsEqualTo("active")
      .Because("an event that does not touch a promoted field must not overwrite its column with a default");
    await Assert.That(afterSecond.Priority).IsEqualTo(7);
    await Assert.That(afterSecond.Tier).IsEqualTo((int)DapperSplitReloadTier.Gold)
      .Because("an enum column holds the member's underlying number");
    await Assert.That(afterSecond.PlacedAt).IsEqualTo(_placedAt);
  }

  [Test]
  public async Task GetByStreamIdAsync_ReturnsThePromotedFieldsFromTheirColumnsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, streamId, _statusSet(streamId, "queued", 3));
    await _appendAsync(eventStore, streamId, new DapperSplitReloadNoteChangedEvent { StreamId = streamId, Note = "first" });
    await _runner(eventStore).RunAsync(streamId, TABLE_NAME, null, CancellationToken.None);

    var model = await _store().GetByStreamIdAsync(streamId);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Status).IsEqualTo("queued")
      .Because("a Split field lives only in its column, so the load must read it from there");
    await Assert.That(model.Priority).IsEqualTo(3);
    await Assert.That(model.Tier).IsEqualTo(DapperSplitReloadTier.Gold)
      .Because("an enum is written as its number and read back from it");
    await Assert.That(model.PlacedAt).IsEqualTo(_placedAt);
    await Assert.That(model.Note).IsEqualTo("first");
  }

  [Test]
  public async Task GetByStreamIdAsync_ANullColumn_LoadsAsNullAsync() {
    var streamId = Guid.CreateVersion7();
    await _store().UpsertWithPhysicalFieldsAsync(streamId, new DapperSplitReloadModel { Id = streamId, Note = "bare" },
      new Dictionary<string, object?> { ["status"] = null, ["priority"] = 0, ["tier"] = null, ["placed_at"] = null });

    var model = await _store().GetByStreamIdAsync(streamId);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Status).IsNull();
    await Assert.That(model.Tier).IsNull();
    await Assert.That(model.PlacedAt).IsNull();
    await Assert.That(model.Note).IsEqualTo("bare");
  }
  /// <summary>A model whose map is registered by hand, to reach the column shapes the fixture does not declare.</summary>
  internal sealed class VectorModel {
    public float[]? Embedding { get; set; }
    public string? Label { get; set; }
  }

  private static void _registerVectorMap(string column) =>
    SplitPhysicalFieldRegistry.Register(new SplitPhysicalFieldMap<VectorModel>(
        [new SplitPhysicalColumn(column, IsVector: true)],
        static (model, read) => {
          model.Embedding = read.GetVector("embedding");
          return model;
        }));

  private DapperPostgresPerspectiveStore<VectorModel> _vectorStore() => new(ConnectionString, "wh_per_dapper_split_vector", new JsonSerializerOptions {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(DapperSplitVectorJsonContext.Default, global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
  });

  private async Task _createVectorTableAsync(Guid id, string embedding) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($$"""
      CREATE TABLE wh_per_dapper_split_vector (id UUID PRIMARY KEY, data JSONB NOT NULL, embedding REAL[]);
      INSERT INTO wh_per_dapper_split_vector (id, data, embedding) VALUES (@id, '{"Label":"v"}', {{embedding}});
      """, connection);
    command.Parameters.AddWithValue(nameof(id), id);
    await command.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task GetByStreamIdAsync_AVectorColumn_LoadsItsComponentsAsync() {
    var id = Guid.CreateVersion7();
    await _createVectorTableAsync(id, "'{1,2,3}'");
    _registerVectorMap("embedding");

    var model = await _vectorStore().GetByStreamIdAsync(id);

    await Assert.That(model!.Embedding).IsEquivalentTo(_components);
    await Assert.That(model.Label).IsEqualTo("v");
  }

  [Test]
  public async Task GetByStreamIdAsync_ANullVectorColumn_LoadsAsNullAsync() {
    var id = Guid.CreateVersion7();
    await _createVectorTableAsync(id, "NULL");
    _registerVectorMap("embedding");

    var model = await _vectorStore().GetByStreamIdAsync(id);

    await Assert.That(model!.Embedding).IsNull();
  }

  [Test]
  public async Task GetByStreamIdAsync_AColumnTheMapDoesNotList_ThrowsAsync() {
    var id = Guid.CreateVersion7();
    await _createVectorTableAsync(id, "NULL");
    // The copy asks for "embedding", but the row was read with a differently named column.
    SplitPhysicalFieldRegistry.Register(new SplitPhysicalFieldMap<VectorModel>(
        [new SplitPhysicalColumn("id", IsVector: false)],
        static (model, read) => {
          model.Embedding = read.GetVector("embedding");
          return model;
        }));

    await Assert.That(async () => await _vectorStore().GetByStreamIdAsync(id))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task GetByStreamIdAsync_AColumnThatIsNotAPlainName_ThrowsBeforeConnectingAsync() {
    _registerVectorMap("embedding; DROP TABLE x");

    await Assert.That(async () => await _vectorStore().GetByStreamIdAsync(Guid.CreateVersion7()))
      .Throws<InvalidOperationException>();
  }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(DapperSplitPhysicalFieldReloadTests.VectorModel))]
internal sealed partial class DapperSplitVectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
