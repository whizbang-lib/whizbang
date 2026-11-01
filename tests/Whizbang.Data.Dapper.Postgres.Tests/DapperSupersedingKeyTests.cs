// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Data;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>A superseding event appended through the Dapper store, keyed by its model.</summary>
[Superseding]
public sealed record DapperSupersedingProbeEmbedded : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }

  /// <summary>The key.</summary>
  [SupersedingKey]
  public required string Model { get; init; }
}

/// <summary>
/// The Dapper store's direct append stamps a superseding event's key (#1729): the adapter writes it into the stored
/// metadata as <c>sk</c>, and <c>wh_append_event</c> copies it onto the flagged row and queues the older event of the key.
/// </summary>
/// <docs>fundamentals/events/superseding-events#the-key</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/EventEnvelopeJsonbAdapter.cs</code-under-test>
public class DapperSupersedingKeyTests : PostgresTestBase {
  [Test]
  public async Task AppendAsync_ASupersedingEvent_IsKeyed_AndTheOlderOfItsKeyIsQueuedAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    await store.AppendAsync(stream, new DapperSupersedingProbeEmbedded { StreamId = stream, Model = "model-a" });
    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    await store.AppendAsync(stream, new DapperSupersedingProbeEmbedded { StreamId = stream, Model = "model-a" });

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var keys = (await conn.QueryAsync<string?>(
      "SELECT supersede_key FROM wh_event_store WHERE stream_id = @stream ORDER BY version", new { stream })).ToList();
    await Assert.That(keys).IsEquivalentTo(new string?[] { "model-a", null, "model-a" }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the superseding events carry their key and the ordinary event none");
    var queued = await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_superseded_events q JOIN wh_event_store es ON es.event_id = q.event_id " +
      "WHERE es.stream_id = @stream AND es.version = 1", new { stream });
    await Assert.That(queued).IsEqualTo(1L).Because("the first event of the key is superseded by the third");
  }

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }
}
