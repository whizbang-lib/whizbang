// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres.Tests.Generated;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1020 on the Dapper driver: a promoted object, list, array or dictionary is written to its jsonb column as
/// JSON under the store's options, and the model the next event is applied to reads it back, on an Extracted model
/// and on a Split one. Before this an object was written as its type's name and an array as a native array the
/// jsonb column refused.
/// </summary>
/// <remarks>Driven through the generated runner, so the load, the apply and the write are the ones an application runs.</remarks>
[NotInParallel("PostgreSQL")]
public class DapperJsonbColumnTests : PostgresTestBase {
  private const string EXTRACTED_TABLE = "wh_per_dapper_jsonb_extracted";
  private const string SPLIT_TABLE = "wh_per_dapper_jsonb_split";
  private static readonly string[] _northEast = ["north", "east"];
  private static readonly Guid _owner = Guid.Parse("0190f0a0-0000-7000-8000-000000000001");

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
      DapperJsonbColumnJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
  };

  [Before(Test)]
  public async Task CreateTablesAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    foreach (var table in new[] { EXTRACTED_TABLE, SPLIT_TABLE }) {
      await using var command = new NpgsqlCommand($"""
        CREATE TABLE {table} (
          id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
          created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL,
          filters JSONB, labels JSONB, place JSONB, owners JSONB);
        """, connection);
      await command.ExecuteNonQueryAsync();
    }
  }

  private static DapperJsonbFiltersSetEvent _filtersSet(Guid streamId) => new() {
    StreamId = streamId,
    Filters = new() { ["region"] = ["north", "east"] },
    Labels = [new("team", "a")],
    Place = new("Springfield", 3),
    Owners = [_owner],
  };

  private static ServiceProvider _services<TPerspective>() where TPerspective : class {
    var services = new ServiceCollection();
    services.AddTransient<TPerspective>();
    services.AddLogging();
    return services.BuildServiceProvider();
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

  private async Task<string> _columnsAsync(string table, Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"SELECT concat_ws('|', filters::text, labels::text, place::text, owners::text, data ->> 'Note') FROM {table} WHERE id = @id", connection);
    command.Parameters.AddWithValue("id", streamId);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  private const string EXPECTED_COLUMNS =
    "{\"region\": [\"north\", \"east\"]}|[{\"Key\": \"team\", \"Value\": \"a\"}]|{\"City\": \"Springfield\", \"Zone\": 3}|[\"0190f0a0-0000-7000-8000-000000000001\"]|second";

  [Test]
  public async Task ExtractedModel_JsonbColumns_SurviveAnEventThatLeavesThemAloneAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var store = new DapperPostgresPerspectiveStore<DapperJsonbExtractedModel>(ConnectionString, EXTRACTED_TABLE, _jsonOptions);
    var provider = _services<DapperJsonbExtractedPerspective>();
    DapperJsonbExtractedPerspectiveRunner runner() => new(
      provider, provider.GetRequiredService<ILogger<DapperJsonbExtractedPerspectiveRunner>>(), eventStore, store,
      provider.GetRequiredService<IServiceScopeFactory>());

    await _appendAsync(eventStore, streamId, _filtersSet(streamId));
    var first = await runner().RunAsync(streamId, EXTRACTED_TABLE, null, CancellationToken.None);
    await _appendAsync(eventStore, streamId, new DapperJsonbNoteChangedEvent { StreamId = streamId, Note = "second" });
    await runner().RunAsync(streamId, EXTRACTED_TABLE, first.LastEventId, CancellationToken.None);

    await Assert.That(await _columnsAsync(EXTRACTED_TABLE, streamId)).IsEqualTo(EXPECTED_COLUMNS);
    var model = await store.GetByStreamIdAsync(streamId);
    await Assert.That(model!.Filters["region"]).IsEquivalentTo(_northEast);
    await Assert.That(model.Place).IsEqualTo(new DapperJsonbPlace("Springfield", 3));
  }

  [Test]
  public async Task SplitModel_JsonbColumns_SurviveAnEventThatLeavesThemAloneAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    var store = new DapperPostgresPerspectiveStore<DapperJsonbSplitModel>(ConnectionString, SPLIT_TABLE, _jsonOptions);
    var provider = _services<DapperJsonbSplitPerspective>();
    DapperJsonbSplitPerspectiveRunner runner() => new(
      provider, provider.GetRequiredService<ILogger<DapperJsonbSplitPerspectiveRunner>>(), eventStore, store,
      provider.GetRequiredService<IServiceScopeFactory>());

    await _appendAsync(eventStore, streamId, _filtersSet(streamId));
    var first = await runner().RunAsync(streamId, SPLIT_TABLE, null, CancellationToken.None);
    await _appendAsync(eventStore, streamId, new DapperJsonbNoteChangedEvent { StreamId = streamId, Note = "second" });
    await runner().RunAsync(streamId, SPLIT_TABLE, first.LastEventId, CancellationToken.None);

    await Assert.That(await _columnsAsync(SPLIT_TABLE, streamId)).IsEqualTo(EXPECTED_COLUMNS);
    var model = await store.GetByStreamIdAsync(streamId);
    await Assert.That(model!.Labels).IsEquivalentTo([new DapperJsonbLabel("team", "a")]);
    await Assert.That(model.Owners).IsEquivalentTo([_owner]);
  }

  [Test]
  public async Task SplitModel_ANullJsonbColumn_ReadsBackAsNullAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new DapperPostgresPerspectiveStore<DapperJsonbSplitModel>(ConnectionString, SPLIT_TABLE, _jsonOptions);
    await store.UpsertWithPhysicalFieldsAsync(streamId, new DapperJsonbSplitModel { Id = streamId },
      new Dictionary<string, object?> { ["filters"] = new Dictionary<string, string[]>(), ["labels"] = null, ["place"] = null, ["owners"] = new List<Guid>() });

    var model = await store.GetByStreamIdAsync(streamId);

    await Assert.That(model!.Labels).IsNull();
    await Assert.That(model.Place).IsNull();
    await Assert.That(await _columnsAsync(SPLIT_TABLE, streamId)).IsEqualTo("{}|[]");
  }
}
