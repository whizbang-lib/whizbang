// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
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
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

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

/// <summary>A superseding event keyed by a value type, whose older events may lack the key (#2123).</summary>
[Superseding]
public sealed record DapperSupersedingProbeRegioned : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }

  /// <summary>The key.</summary>
  [SupersedingKey]
  public required Guid RegionId { get; init; }
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
    await Assert.That(keys).IsEquivalentTo(["model-a", null, "model-a"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the superseding events carry their key and the ordinary event none");
    var queued = await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_superseded_events q JOIN wh_event_store es ON es.event_id = q.event_id " +
      "WHERE es.stream_id = @stream AND es.version = 1", new { stream });
    await Assert.That(queued).IsEqualTo(1L).Because("the first event of the key is superseded by the third");
  }

  /// <summary>
  /// #2110: on the Dapper driver too, events stored before their type was declared superseding are stamped and queued by
  /// the registered maintenance step, and the reaper then keeps only the newest of each key once they are consumed.
  /// </summary>
  [Test]
  public async Task HistoryStoredBeforeTheDeclaration_IsReconciledByTheRegisteredStep_AndReapedToTheNewestAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    await store.AppendAsync(stream, new DapperSupersedingProbeEmbedded { StreamId = stream, Model = "model-a" });
    await store.AppendAsync(stream, new DapperSupersedingProbeEmbedded { StreamId = stream, Model = "model-a" });
    await store.AppendAsync(stream, new DapperSupersedingProbeEmbedded { StreamId = stream, Model = "model-b" });
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // As the rows were stored before the declaration: no flag, no key, nothing queued.
    _ = await conn.ExecuteAsync("UPDATE wh_event_store SET flags = flags & ~128, supersede_key = NULL WHERE stream_id = @stream", new { stream });
    _ = await conn.ExecuteAsync("DELETE FROM wh_superseded_events");
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<SupersedingHistoryReconcileStep>().ToList();
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);
    _ = await conn.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    var reaped = await conn.ExecuteScalarAsync<long>("SELECT rows_affected FROM wh_reap_superseded_events(5000)");

    var kept = (await conn.QueryAsync<int>("SELECT version FROM wh_event_store WHERE stream_id = @stream ORDER BY version", new { stream })).ToList();
    await Assert.That(reaped).IsEqualTo(1L);
    await Assert.That(kept).IsEquivalentTo([2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the older event of key model-a goes; the newest of each key stays");
  }

  /// <summary>
  /// #2123: on the Dapper driver, events stored before their value-type key existed lack it in the stored payload. The
  /// reconcile stamps them without a key rather than as the type's default, so the reaper keeps every one of them.
  /// </summary>
  [Test]
  public async Task HistoryMissingAValueTypeKey_IsReconciledUnkeyed_AndNoneIsReapedAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    for (var i = 0; i < 3; i++) {
      await store.AppendAsync(stream, new DapperSupersedingProbeRegioned { StreamId = stream, RegionId = TrackedGuid.New() });
    }
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // As the rows were stored before the key property existed: no flag, no key, no key in the payload, nothing queued.
    _ = await conn.ExecuteAsync("UPDATE wh_event_store SET flags = flags & ~128, supersede_key = NULL WHERE stream_id = @stream", new { stream });
    _ = await conn.ExecuteAsync(
      "UPDATE wh_event_body b SET event_data = b.event_data - 'RegionId' FROM wh_event_store es " +
      "WHERE es.event_id = b.event_id AND es.stream_id = @stream", new { stream });
    _ = await conn.ExecuteAsync("DELETE FROM wh_superseded_events");
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<SupersedingHistoryReconcileStep>().ToList();
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);
    _ = await conn.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    var reaped = await conn.ExecuteScalarAsync<long>("SELECT rows_affected FROM wh_reap_superseded_events(5000)");

    var rows = (await conn.QueryAsync<(int Version, int Flags, string? Key)>(
      "SELECT version, COALESCE(flags, 0)::int, supersede_key FROM wh_event_store WHERE stream_id = @stream ORDER BY version", new { stream })).ToList();
    await Assert.That(reaped).IsEqualTo(0L).Because("an event whose key is unknown is never superseded");
    await Assert.That(rows.Select(r => r.Version)).IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(rows.All(r => (r.Flags & 128) == 128 && r.Key is null)).IsTrue()
      .Because("each is stamped superseding without a key, not with the Guid default they would all share");
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
