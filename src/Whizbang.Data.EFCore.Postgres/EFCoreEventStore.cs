using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Entities;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IEventStore using PostgreSQL with JSONB columns.
/// Provides append-only event storage with optimistic concurrency control.
/// </summary>
public sealed class EFCoreEventStore<TDbContext> : IEventStore
  where TDbContext : DbContext {

  private readonly TDbContext _context;
  private readonly string _tableName;

  public EFCoreEventStore(TDbContext context) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _tableName = "event_store"; // Default table name, can be configured
  }

  public async Task AppendAsync(
      string streamId,
      long expectedVersion,
      IEnumerable<object> events,
      CancellationToken cancellationToken = default) {

    if (string.IsNullOrWhiteSpace(streamId)) {
      throw new ArgumentException("Stream ID cannot be null or whitespace.", nameof(streamId));
    }

    if (expectedVersion < 0) {
      throw new ArgumentException("Expected version must be non-negative.", nameof(expectedVersion));
    }

    var eventList = events?.ToList() ?? throw new ArgumentNullException(nameof(events));
    if (eventList.Count == 0) {
      return; // Nothing to append
    }

    // Check current stream version for optimistic concurrency
    var currentVersion = await GetStreamVersionAsync(streamId, cancellationToken);
    if (currentVersion != expectedVersion) {
      throw new InvalidOperationException(
        $"Concurrency conflict: Expected version {expectedVersion} but stream is at version {currentVersion}");
    }

    // Append events with sequential versions
    var nextVersion = expectedVersion + 1;
    var records = new List<EventStoreRecord>();

    foreach (var @event in eventList) {
      var eventType = @event.GetType().FullName
        ?? throw new InvalidOperationException($"Event type has no FullName: {@event.GetType()}");

      // Serialize event to JSON
      var eventDataJson = JsonSerializer.Serialize(@event);
      var eventData = JsonDocument.Parse(eventDataJson);

      // Create metadata (placeholder - should come from MessageEnvelope)
      var metadataJson = JsonSerializer.Serialize(new {
        Timestamp = DateTime.UtcNow,
        CorrelationId = Guid.NewGuid().ToString(),
        CausationId = Guid.NewGuid().ToString()
      });
      var metadata = JsonDocument.Parse(metadataJson);

      var record = new EventStoreRecord {
        StreamId = streamId,
        Version = nextVersion++,
        EventType = eventType,
        EventData = eventData,
        Metadata = metadata,
        Scope = null,
        CreatedAt = DateTime.UtcNow
      };

      records.Add(record);
    }

    // Add all records in batch
    await _context.Set<EventStoreRecord>().AddRangeAsync(records, cancellationToken);
    await _context.SaveChangesAsync(cancellationToken);
  }

  public async Task<IEnumerable<object>> ReadStreamAsync(
      string streamId,
      long fromVersion = 1,
      CancellationToken cancellationToken = default) {

    if (string.IsNullOrWhiteSpace(streamId)) {
      throw new ArgumentException("Stream ID cannot be null or whitespace.", nameof(streamId));
    }

    var records = await _context.Set<EventStoreRecord>()
      .Where(e => e.StreamId == streamId && e.Version >= fromVersion)
      .OrderBy(e => e.Version)
      .ToListAsync(cancellationToken);

    var events = new List<object>();
    foreach (var record in records) {
      // Deserialize event (requires type resolution - simplified for now)
      var eventType = Type.GetType(record.EventType);
      if (eventType == null) {
        throw new InvalidOperationException($"Cannot resolve event type: {record.EventType}");
      }

      var @event = JsonSerializer.Deserialize(record.EventData.RootElement.GetRawText(), eventType)
        ?? throw new InvalidOperationException($"Failed to deserialize event: {record.EventType}");

      events.Add(@event);
    }

    return events;
  }

  private async Task<long> GetStreamVersionAsync(string streamId, CancellationToken cancellationToken) {
    var maxVersion = await _context.Set<EventStoreRecord>()
      .Where(e => e.StreamId == streamId)
      .MaxAsync(e => (long?)e.Version, cancellationToken);

    return maxVersion ?? 0; // Stream doesn't exist yet, version is 0
  }
}
