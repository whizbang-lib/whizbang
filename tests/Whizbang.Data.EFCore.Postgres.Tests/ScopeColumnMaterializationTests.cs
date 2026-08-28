using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Pins that a scope persisted by a writer OTHER than EF still reaches the hop chain on read.
/// </summary>
/// <remarks>
/// <para>
/// Every existing scope test in this project writes its row through EF and reads it back through
/// EF. That round-trip is self-consistent by construction: whatever shape EF writes is the shape
/// EF reads, so the assertions hold no matter which shape that is. The scope column in a running
/// deployment is not written by EF — the ingest path writes it — and no test covered that
/// asymmetry.
/// </para>
/// <para>
/// It matters because the read path bails out when the materialized scope has no values:
/// <c>ScopeDelta.FromPerspectiveScope</c> returns null for an all-empty scope, the hop keeps a null
/// ScopeDelta, and <c>MessageHopSecurityExtractor</c> reports "no scope found in hop chain". A
/// perspective requiring a security context then throws on every event, retries until it exhausts
/// its attempts, and parks. The projection stops converging while the column it needed was
/// populated the whole time.
/// </para>
/// <para>
/// So these tests write the row through EF (keeping body and metadata valid) and then rewrite ONLY
/// the scope column to the shape a deployment actually stores. That isolates the single variable
/// under test. The scope literal is deliberately hand-written here rather than serialized from a
/// real <see cref="PerspectiveScope"/>: reproducing the stored bytes IS the test. The first test
/// below pins that literal against the real serializer, so if the two ever diverge it fails loudly
/// instead of quietly testing a shape nothing writes.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Shard3")]
public class ScopeColumnMaterializationTests : EFCoreTestBase {

  private const string TenantId = "c0ffee00-cafe-f00d-face-feed12345678";
  private const string UserId = "2ef787e2-bf41-4f3b-a968-28e7099c20bc";

  /// <summary>The scope shape a deployment actually stores: short keys, arrays present.</summary>
  private static string ProductionScopeJson =>
    $$"""{"t": "{{TenantId}}", "u": "{{UserId}}", "ap": [], "ex": []}""";

  [Test]
  [Category("Integration")]
  public async Task TheProductionScopeLiteralMatchesWhatTheRealSerializerProducesAsync() {
    // Guards every other test in this file. If PerspectiveScope's wire shape ever changes, the
    // hand-written literal silently stops representing a stored row and the tests below would pass
    // against a shape nothing writes.
    var scope = new PerspectiveScope { TenantId = TenantId, UserId = UserId };
    var serialized = System.Text.Json.JsonSerializer.SerializeToElement(scope);

    await Assert.That(serialized.TryGetProperty("t", out var t)).IsTrue()
      .Because("the stored column uses the short key 't'; if the serializer emits 'TenantId' "
             + "instead, the literal under test no longer matches reality");
    await Assert.That(t.GetString()).IsEqualTo(TenantId);
    await Assert.That(serialized.TryGetProperty("u", out var u)).IsTrue();
    await Assert.That(u.GetString()).IsEqualTo(UserId);
  }

  [Test]
  [Category("Integration")]
  public async Task ScopeStoredByANonEfWriterReachesTheHopChainAsync() {
    // The production shape, on the method a perspective actually loads through.
    var streamId = Guid.CreateVersion7();
    var eventId = await _seedEventThenOverwriteScopeAsync(streamId, ProductionScopeJson, withHop: true);

    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: eventId, [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1);

    var scope = envelopes[0].GetCurrentScope();
    await Assert.That(scope).IsNotNull()
      .Because("the scope column was populated, so a security context IS establishable — dropping "
             + "it here is what makes a requiring perspective throw on every event and park");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo(TenantId);
    await Assert.That(scope.Scope.UserId).IsEqualTo(UserId);
  }

  [Test]
  [Category("Integration")]
  public async Task AnExistingHopWithoutScopeStillReceivesTheStoredScopeAsync() {
    // The exact production shape: one Current hop that carries no ScopeDelta, alongside a populated
    // scope column. This is what the extractor reports as "TotalHops=1, CurrentHops=1" immediately
    // before "No scope found in hop chain".
    var streamId = Guid.CreateVersion7();
    var eventId = await _seedEventThenOverwriteScopeAsync(streamId, ProductionScopeJson, withHop: true);

    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: eventId, [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Hops).IsNotEmpty();
    await Assert.That(envelopes[0].Hops[0].Scope).IsNotNull()
      .Because("a hop that already exists but carries no scope must still receive the stored one; "
             + "leaving it null is indistinguishable from an event that was never scoped");
  }

  [Test]
  [Category("Integration")]
  public async Task TheRealExtractorEstablishesAContextFromAStoredScopeAsync() {
    // Drives the actual extractor rather than asserting on hops, because that is the component
    // whose null return becomes SecurityContextRequiredException.
    var streamId = Guid.CreateVersion7();
    var eventId = await _seedEventThenOverwriteScopeAsync(streamId, ProductionScopeJson, withHop: true);

    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);
    var envelopes = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: eventId, [typeof(OrderCreatedEvent)]);

    var extraction = await new MessageHopSecurityExtractor()
      .ExtractAsync(envelopes[0], new MessageSecurityOptions());

    await Assert.That(extraction).IsNotNull()
      .Because("a null extraction is precisely what reaches _handleNoExtraction and throws "
             + "SecurityContextRequiredException — the failure this whole path exists to prevent");
    await Assert.That(extraction!.Scope.TenantId).IsEqualTo(TenantId);
  }

  [Test]
  [Category("Integration")]
  public async Task AnEventWithNoStoredScopeStillYieldsNoScopeAsync() {
    // The other half of the contract. Restoring what was persisted must never become inventing
    // authority that was not.
    var streamId = Guid.CreateVersion7();
    var eventId = await _seedEventThenOverwriteScopeAsync(streamId, scopeJson: null, withHop: true);

    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: eventId, [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].GetCurrentScope()).IsNull()
      .Because("an unscoped event must stay unscoped; fabricating a tenant here would be worse "
             + "than the exception, because it would silently project one tenant's data as another's");
  }

  // === Drain mode ===
  //
  // Drain mode does NOT load events through GetEventsBetweenPolymorphicAsync. It fetches raw rows
  // via get_stream_events and builds envelopes with DeserializeStreamEvents, then hands THOSE
  // envelopes straight to the lifecycle receptors. So the tests above can all pass while the path a
  // busy deployment actually runs stays broken.

  [Test]
  [Category("Integration")]
  public async Task DrainModeDeserializationCarriesTheScopeOntoTheHopAsync() {
    // Exactly what the drain path receives: raw row + scope column + a hop with no ScopeDelta.
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();

    var raw = await _readStoredRowAsStreamEventAsync(streamId, ProductionScopeJson, withHop: true);

    var envelopes = eventStore.DeserializeStreamEvents([raw], [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1)
      .Because("a row that fails to deserialize is skipped silently, which would make every "
             + "assertion below vacuous rather than failing");
    await Assert.That(envelopes[0].GetCurrentScope()).IsNotNull()
      .Because("drain mode hands these envelopes straight to the lifecycle receptors, so a scope "
             + "dropped here is the SecurityContextRequiredException that parks the event");
    await Assert.That(envelopes[0].GetCurrentScope()!.Scope.TenantId).IsEqualTo(TenantId);
  }

  [Test]
  [Category("Integration")]
  public async Task DrainModeCarriesScopeWhenTheRowHasNoHopsAtAllAsync() {
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();

    var raw = await _readStoredRowAsStreamEventAsync(streamId, ProductionScopeJson, withHop: false);

    var envelopes = eventStore.DeserializeStreamEvents([raw], [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].GetCurrentScope()).IsNotNull()
      .Because("an event read back with no hops still has its scope in the column; dropping it "
             + "leaves the event permanently unprocessable for a requiring perspective");
  }

  [Test]
  [Category("Integration")]
  public async Task DrainModeEnvelopeSatisfiesTheRealSecurityExtractorAsync() {
    // The end of the chain: what the drain envelope produces is what decides throw-or-proceed.
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();

    var raw = await _readStoredRowAsStreamEventAsync(streamId, ProductionScopeJson, withHop: true);

    var envelopes = eventStore.DeserializeStreamEvents([raw], [typeof(OrderCreatedEvent)]);
    var extraction = await new MessageHopSecurityExtractor()
      .ExtractAsync(envelopes[0], new MessageSecurityOptions());

    await Assert.That(extraction).IsNotNull()
      .Because("null here is literally the throw: _handleNoExtraction raises "
             + "SecurityContextRequiredException and the perspective parks the event");
    await Assert.That(extraction!.Scope.TenantId).IsEqualTo(TenantId);
  }


  [Test]
  [Category("Integration")]
  public async Task AHopSerializedByAnUpstreamPublisherStillGetsTheStoredScopeAsync() {
    // The hop shape a publisher actually writes, reproduced key-for-key with generic values:
    // causation, correlation, composite-type, service-instance, timestamp — and NO scope key.
    // The scope lives only in the column.
    //
    // The tests above build their hop by serializing a MessageHop, so they can only ever produce
    // the shape THIS assembly's serializer emits. A hop written by a different service is not
    // guaranteed to match that, and the difference is invisible until a hop round-trips to
    // something the extractor cannot read.
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();

    var metadata = $$"""
      {
        "MessageId": "{{Guid.CreateVersion7()}}",
        "Hops": [{
          "ca": "{{Guid.CreateVersion7()}}",
          "co": "{{Guid.CreateVersion7()}}",
          "ct": "SampleEventsComposite",
          "si": {
            "hn": "sample-host",
            "ii": "{{Guid.CreateVersion7()}}",
            "sn": "sample-service",
            "pi": 1
          },
          "ts": "2026-01-01T00:00:00.0000000+00:00"
        }]
      }
      """;

    var raw = new StreamEventData {
      StreamId = streamId,
      EventId = Guid.CreateVersion7(),
      EventWorkId = Guid.CreateVersion7(),
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}",
      Metadata = metadata,
      Scope = ProductionScopeJson,
    };

    var envelopes = eventStore.DeserializeStreamEvents([raw], [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1)
      .Because("a metadata shape the deserializer rejects makes the row SKIP, which would show up "
             + "as a scope failure that is really a fixture failure");
    await Assert.That(envelopes[0].GetCurrentScope()).IsNotNull()
      .Because("this is the dominant failing shape in a real deployment: a hop with no scope key "
             + "beside a populated scope column, on the path that feeds lifecycle receptors");
    await Assert.That(envelopes[0].GetCurrentScope()!.Scope.TenantId).IsEqualTo(TenantId);
  }

  [Test]
  [Category("Integration")]
  public async Task AJsonNullScopeIsTreatedAsNoScopeRatherThanAsAValueAsync() {
    // A jsonb column holding the JSON null LITERAL is not SQL NULL. The distinction is easy to miss
    // and changes the answer twice over:
    //
    //   - In SQL, "WHERE scope IS NULL" does not match 'null'::jsonb, so a column full of JSON nulls
    //     counts as fully populated. Auditing stored scope with that predicate reports healthy data
    //     and sends the investigation at the read path instead of the writer.
    //   - In C#, the string arriving from the column is "null", which is not empty, so the
    //     IsNullOrEmpty guard does not fire and the value goes to the deserializer.
    //
    // The deserializer returns null and the read path then correctly restores nothing. This test
    // pins that: an event stored without a scope must stay unscoped, and must not throw or invent
    // one on the way through.
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.CreateVersion7();

    var raw = await _readStoredRowAsStreamEventAsync(streamId, scopeJson: "null", withHop: true);

    await Assert.That(raw.Scope).IsEqualTo("null")
      .Because("the column must actually hold the JSON null literal for this test to exercise the "
             + "path it claims to — a SQL NULL would short-circuit on IsNullOrEmpty instead");

    var envelopes = eventStore.DeserializeStreamEvents([raw], [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes).Count().IsEqualTo(1)
      .Because("an unscoped event must still deserialize; dropping it would turn missing security "
             + "context into missing data");
    await Assert.That(envelopes[0].GetCurrentScope()).IsNull()
      .Because("nothing was stored, so nothing can be restored — fabricating a scope here would "
             + "project one tenant's data under another's authority");
  }

  /// <summary>
  /// Seeds one event and reads the ACTUAL stored bytes back out as a <see cref="StreamEventData"/>,
  /// which is what the drain path receives from <c>get_stream_events</c>.
  /// </summary>
  /// <remarks>
  /// Hand-authoring the event_data / metadata strings looks equivalent and is not: a shape the
  /// deserializer rejects makes it SKIP the row, so the batch comes back empty and every assertion
  /// about scope reads as a failure for a reason that has nothing to do with scope. Sourcing the
  /// bytes from the row that was actually written removes that whole class of false result.
  /// </remarks>
  private async Task<StreamEventData> _readStoredRowAsStreamEventAsync(
      Guid streamId, string? scopeJson, bool withHop) {
    var eventId = await _seedEventThenOverwriteScopeAsync(streamId, scopeJson, withHop);

    await using var context = CreateDbContext();
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text
      FROM wh_event_store es
      JOIN wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.event_id = @id
      """;
    cmd.Parameters.AddWithValue("id", eventId);

    await using var reader = await cmd.ExecuteReaderAsync();
    var found = await reader.ReadAsync();
    await Assert.That(found).IsTrue()
      .Because("without the seeded row there is nothing to deserialize and the test would report "
             + "an empty batch as if scope handling had failed");

    return new StreamEventData {
      StreamId = streamId,
      EventId = eventId,
      EventWorkId = Guid.CreateVersion7(),
      EventType = reader.GetString(0),
      EventData = reader.GetString(1),
      Metadata = reader.IsDBNull(2) ? null : reader.GetString(2),
      Scope = reader.IsDBNull(3) ? null : reader.GetString(3),
    };
  }

  /// <summary>
  /// Seeds one event through EF so body and metadata are valid, then rewrites ONLY the scope column
  /// via raw SQL to the given literal (or NULL). Isolates the stored-scope shape as the single
  /// variable, without hand-authoring metadata JSON that could itself be wrong.
  /// </summary>
  private async Task<Guid> _seedEventThenOverwriteScopeAsync(
      Guid streamId, string? scopeJson, bool withHop) {
    var eventId = Guid.CreateVersion7();

    await using (var context = CreateDbContext()) {
      var record = new EventStoreRecord {
        Id = eventId,
        StreamId = streamId,
        AggregateId = streamId,
        AggregateType = "TestAggregate",
        Version = 1,
        EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
        EventData = System.Text.Json.JsonDocument.Parse(
          $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
        Metadata = new EnvelopeMetadata {
          MessageId = MessageId.From(Guid.CreateVersion7()),
          // A hop with NO ScopeDelta — the shape a deployment shows on the failing events.
          Hops = withHop ? [_hopWithoutScope()] : [],
        },
        // Deliberately left null: the scope arrives via the raw UPDATE below, standing in for the
        // non-EF writer that populates this column in a running deployment.
        Scope = null,
        CreatedAt = DateTime.UtcNow,
      };

      context.Set<EventStoreRecord>().Add(record);
      context.Set<EventBodyRecord>().Add(new EventBodyRecord {
        EventId = record.Id,
        EventData = record.EventData!.Value,
        Metadata = record.Metadata!,
      });
      await context.SaveChangesAsync();
    }

    await using (var sqlContext = CreateDbContext()) {
      var conn = (NpgsqlConnection)sqlContext.Database.GetDbConnection();
      if (conn.State != System.Data.ConnectionState.Open) {
        await conn.OpenAsync();
      }

      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "UPDATE wh_event_store SET scope = @scope::jsonb WHERE event_id = @id";
      cmd.Parameters.AddWithValue("scope", (object?)scopeJson ?? DBNull.Value);
      cmd.Parameters.AddWithValue("id", eventId);
      var affected = await cmd.ExecuteNonQueryAsync();

      // A silently-zero UPDATE would leave every assertion below testing an unscoped row and
      // "passing" for the wrong reason.
      await Assert.That(affected).IsEqualTo(1)
        .Because("the seeded row must exist for the scope overwrite to mean anything");
    }

    return eventId;
  }

  private static MessageHop _hopWithoutScope() => new() {
    Type = HopType.Current,
    Timestamp = DateTime.UtcNow,
    ServiceInstance = new ServiceInstanceInfo {
      InstanceId = Guid.CreateVersion7(),
      ServiceName = "test-service",
      HostName = "test-host",
      ProcessId = 123,
    },
  };
}
