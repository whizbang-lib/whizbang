// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A stored event with no recorded hops is read back with the WHOLE scope it was stored with (#1855): tenant,
/// user, customer, organization, allowed principals, extensions and the system and declared-unscoped markers.
/// Every Dapper read path is covered on a real database: the typed read through the envelope adapter, the
/// polymorphic stream read, and the checkpoint-range read the perspective worker uses. Each rebuilt the scope
/// from the tenant and user (or from the four id fields) alone, so a receptor or perspective reading a scope
/// extension after a replay, rebuild or redelivery saw less than the original dispatch did. The EF Core event
/// store already restored the whole scope; these paths now match it.
/// </summary>
/// <docs>fundamentals/security/message-security#hop-scope</docs>
public class DapperStoredScopeReadBackTests : PostgresTestBase {
  private static readonly JsonSerializerOptions _jsonOptions = JsonOptionsHelper.CreateOptions();

  /// <summary>The typed read, which goes through <see cref="EventEnvelopeJsonbAdapter"/>.</summary>
  [Test]
  [Arguments(StoredScopeShape.Full)]
  [Arguments(StoredScopeShape.System)]
  [Arguments(StoredScopeShape.DeclaredUnscoped)]
  public async Task ReadAsync_HoplessRow_RestoresTheWholeStoredScopeAsync(StoredScopeShape shape) {
    var stored = _scope(shape);
    var streamId = await _seedHoplessRowAsync(stored);

    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in _createStore().ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    await Assert.That(events).Count().IsEqualTo(1);
    await _assertRestoredAsync(events[0].GetCurrentScope(), stored);
  }

  /// <summary>The polymorphic stream read.</summary>
  [Test]
  [Arguments(StoredScopeShape.Full)]
  [Arguments(StoredScopeShape.System)]
  [Arguments(StoredScopeShape.DeclaredUnscoped)]
  public async Task ReadPolymorphicAsync_HoplessRow_RestoresTheWholeStoredScopeAsync(StoredScopeShape shape) {
    var stored = _scope(shape);
    var streamId = await _seedHoplessRowAsync(stored);

    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var evt in _createStore().ReadPolymorphicAsync(streamId, fromEventId: null, [typeof(TestEvent)])) {
      events.Add(evt);
    }

    await Assert.That(events).Count().IsEqualTo(1);
    await _assertRestoredAsync(events[0].GetCurrentScope(), stored);
  }

  /// <summary>The checkpoint-range read the perspective worker applies events from.</summary>
  [Test]
  [Arguments(StoredScopeShape.Full)]
  [Arguments(StoredScopeShape.System)]
  [Arguments(StoredScopeShape.DeclaredUnscoped)]
  public async Task GetEventsBetweenPolymorphicAsync_HoplessRow_RestoresTheWholeStoredScopeAsync(StoredScopeShape shape) {
    var stored = _scope(shape);
    var streamId = await _seedHoplessRowAsync(stored);

    var events = await _createStore().GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: Guid.Empty, [typeof(TestEvent)]);

    await Assert.That(events).Count().IsEqualTo(1);
    await _assertRestoredAsync(events[0].GetCurrentScope(), stored);
  }

  /// <summary>The scope shapes a stored row can carry.</summary>
  public enum StoredScopeShape {
    /// <summary>Every id field, allowed principals and extensions.</summary>
    Full,
    /// <summary>The framework's system marker and nothing else.</summary>
    System,
    /// <summary>The author's declared-unscoped marker and nothing else.</summary>
    DeclaredUnscoped,
  }

  private static PerspectiveScope _scope(StoredScopeShape shape) => shape switch {
    StoredScopeShape.Full => new PerspectiveScope {
      TenantId = "tenant-1855",
      UserId = "user-1855",
      CustomerId = "customer-1855",
      OrganizationId = "org-1855",
      AllowedPrincipals = ["group:sales", "user:manager"],
      Extensions = [new ScopeExtension("region", "eu-west"), new ScopeExtension("tier", "gold")],
    },
    StoredScopeShape.System => new PerspectiveScope { IsSystem = true },
    _ => new PerspectiveScope { IsDeclaredUnscoped = true },
  };

  private static string _json(PerspectiveScope scope) =>
    JsonSerializer.Serialize(scope, _jsonOptions.GetTypeInfo(typeof(PerspectiveScope)));

  private static async Task _assertRestoredAsync(ScopeContext? restored, PerspectiveScope stored) {
    await Assert.That(restored).IsNotNull()
      .Because("the row stored a scope; a hopless read must still carry it");
    await Assert.That(_json(restored!.Scope)).IsEqualTo(_json(stored))
      .Because("the restored scope must equal the stored one field for field, extensions, allowed principals "
             + "and markers included, as the EF Core event store restores it");
  }

  /// <summary>
  /// Seeds one event row whose metadata recorded no hops and whose scope column holds <paramref name="scope"/>,
  /// the shape a stored event is read back in. Timestamps come from the database clock.
  /// </summary>
  private async Task<Guid> _seedHoplessRowAsync(PerspectiveScope scope) {
    var streamId = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    var eventType = TypeNameFormatter.Format(typeof(TestEvent));
    var eventData = JsonSerializer.Serialize(
      new TestEvent { StreamId = streamId, Payload = "hopless" }, _jsonOptions.GetTypeInfo(typeof(TestEvent)));

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await Executor.ExecuteAsync(
      connection,
      @"INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
        VALUES
          (@EventId, @StreamId, @StreamId, @EventType, 1, @EventType, @Scope::jsonb, NOW());
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@EventId, @EventData::jsonb, @Metadata::jsonb)",
      new {
        EventId = eventId,
        StreamId = streamId,
        EventType = eventType,
        EventData = eventData,
        Metadata = "{\"message_id\":\"" + eventId + "\",\"hops\":[]}",
        Scope = _json(scope),
      });
    return streamId;
  }

  private DapperPostgresEventStore _createStore() => new(
    ConnectionFactory,
    Executor,
    _jsonOptions,
    new EventEnvelopeJsonbAdapter(_jsonOptions),
    new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
    new PolicyEngine(),
    null,
    NullLogger<DapperPostgresEventStore>.Instance);
}
