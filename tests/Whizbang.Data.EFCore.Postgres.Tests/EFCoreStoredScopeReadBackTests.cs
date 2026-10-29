// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A stored event with no recorded hops is read back by reconciliation with the WHOLE scope it was stored with
/// (#1855): tenant, user, customer, organization, allowed principals, extensions and the system and
/// declared-unscoped markers. Reconciliation re-runs lifecycle stages for an event its perspectives already
/// applied, and rebuilt the event's scope from the tenant and user alone, so a lifecycle receptor reading a
/// scope extension saw less than the original dispatch did. The EF Core event store already restored the whole
/// scope; the same row read through it is the parity reference.
/// </summary>
/// <docs>fundamentals/security/message-security#hop-scope</docs>
[Category("Shard5")]
public class EFCoreStoredScopeReadBackTests : EFCoreTestBase {
  private const string PERSPECTIVE = "P.StoredScope";
  private static readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  [Test]
  [Arguments(StoredScopeShape.Full)]
  [Arguments(StoredScopeShape.System)]
  [Arguments(StoredScopeShape.DeclaredUnscoped)]
  public async Task GetOrphanedLifecycleEventsAsync_HoplessRow_RestoresTheWholeStoredScopeAsync(StoredScopeShape shape) {
    await using var dbContext = CreateDbContext();
    var stored = _scope(shape);
    var eventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent));
    var (streamId, eventId) = await _seedReconcilableHoplessRowAsync(dbContext, eventType, stored);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, _jsonOptions, NullLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance);

    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>> { [eventType] = [PERSPECTIVE] }, TimeSpan.FromHours(1));

    await Assert.That(orphans).Count().IsEqualTo(1)
      .Because("the seeded row is the one event every expected perspective applied without a completion marker");
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);
    await _assertRestoredAsync(orphans[0].Envelope.GetCurrentScope(), stored, "reconciliation");

    // Parity: the EF Core event store reads the same row back with the same scope.
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var read = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromSequence: 0)) {
      read.Add(evt);
    }
    await Assert.That(read).Count().IsEqualTo(1);
    await _assertRestoredAsync(read[0].GetCurrentScope(), stored, "the EF Core event store");
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

  private static string _json<T>(T value) => JsonSerializer.Serialize(value, _jsonOptions.GetTypeInfo(typeof(T)));

  private static async Task _assertRestoredAsync(ScopeContext? restored, PerspectiveScope stored, string path) {
    await Assert.That(restored).IsNotNull()
      .Because($"the row stored a scope; {path} must still carry it when the row kept no hops");
    await Assert.That(_json(restored!.Scope)).IsEqualTo(_json(stored))
      .Because($"{path} must restore the stored scope field for field, extensions, allowed principals and markers included");
  }

  /// <summary>
  /// Seeds one event row with no recorded hops and <paramref name="storedScope"/> in its scope column, plus a
  /// cursor showing its one expected perspective applied it and no completion marker: the shape reconciliation
  /// picks up. Timestamps come from the database clock.
  /// </summary>
  private static async Task<(Guid StreamId, Guid EventId)> _seedReconcilableHoplessRowAsync(
      WorkCoordinationDbContext dbContext, string eventType, PerspectiveScope storedScope) {
    var streamId = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    var eventData = _json(new OrderCreatedEvent { OrderId = streamId, CustomerName = "stored-scope" });
    var metadata = _json(new EnvelopeMetadata { MessageId = MessageId.From(eventId), Hops = [] });

    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', @type, @scope::jsonb, 0, NOW());
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@evt, @data::jsonb, @metadata::jsonb);
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@stream, @perspective, @evt, 1, NOW());";
    cmd.Parameters.AddWithValue("evt", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("scope", _json(storedScope));
    cmd.Parameters.AddWithValue("data", eventData);
    cmd.Parameters.AddWithValue("metadata", metadata);
    cmd.Parameters.AddWithValue("perspective", PERSPECTIVE);
    await cmd.ExecuteNonQueryAsync();
    return (streamId, eventId);
  }
}
