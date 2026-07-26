using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Serialization;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IEventStore using PostgreSQL with JSONB columns.
/// Provides append-only event storage for event sourcing and streaming scenarios.
/// Stores events with stream-based organization using sequence numbers.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs</tests>
#pragma warning disable S2743 // Static diagnostic flag is intentionally per-generic-type (reads same env var)
public sealed class EFCoreEventStore<TDbContext>(
  TDbContext context,
  JsonSerializerOptions? jsonOptions = null,
  ILogger<EFCoreEventStore<TDbContext>>? logger = null) : IEventStore
  where TDbContext : DbContext {

  private readonly TDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? EFCoreJsonContext.CreateCombinedOptions();
  // Null-object default so a drain deserialize failure is ALWAYS logged: DI supplies a real logger in
  // production; the NullLogger fallback only applies to manual construction / a log-free host.
  private readonly ILogger<EFCoreEventStore<TDbContext>> _logger = logger ?? NullLogger<EFCoreEventStore<TDbContext>>.Instance;

  /// <summary>
  /// Body-aware read projection (E1 #13b4-1). Since the body offload (072) an ephemeral event's
  /// payload/metadata live in <c>wh_event_body</c> and the pointer's inline columns are NULL — so reads
  /// must resolve body-first with inline fallback (the same COALESCE the SQL read functions use), and a
  /// projection is required because materializing the full <see cref="EventStoreRecord"/> throws on the
  /// NULL inline columns. A row with BOTH sides NULL is a reaped ephemeral body: consumed,
  /// snapshot-covered history that readers skip rather than throw on.
  /// </summary>
  private sealed class EventRow {
    public Guid Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public int Version { get; init; }
    public long? CommitSequence { get; init; }
    public JsonElement? EventData { get; init; }
    public EnvelopeMetadata? Metadata { get; init; }
    public PerspectiveScope? Scope { get; init; }
  }

  /// <summary>
  /// Projects a (filtered, ordered) pointer query into body-aware rows via LEFT JOIN on
  /// <c>wh_event_body</c>. Apply Where/OrderBy on the pointer query BEFORE calling this.
  /// </summary>
  private IQueryable<EventRow> _bodyAwareRows(IQueryable<EventStoreRecord> pointers) =>
    from e in pointers
    join body in _context.Set<EventBodyRecord>().AsNoTracking()
      on e.Id equals body.EventId into bodies
    from b in bodies.DefaultIfEmpty()
    select new EventRow {
      Id = e.Id,
      EventType = e.EventType,
      Version = e.Version,
      CommitSequence = e.CommitSequence,
      Scope = e.Scope,
      // #13b4-3 (078): the inline columns are dropped — the body table IS the body. A missing body
      // row (reaped ephemeral) surfaces as NULL and the caller skips it.
      EventData = b != null ? (JsonElement?)b.EventData : null,
      Metadata = b != null ? b.Metadata : null,
    };

  /// <summary>
  /// Appends an event to the specified stream.
  /// Assigns the next sequence number automatically.
  /// Ensures optimistic concurrency through unique constraint on (StreamId, Sequence).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithValidEnvelope_AppendsEventToStreamAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithMultipleEvents_AssignsSequentialSequenceNumbersAsync</tests>
  public Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);
    return _appendCoreAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);

    // Create a minimal envelope - registry-based lookup would require constructor injection
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    return AppendAsync(streamId, envelope, cancellationToken);
  }

  private async Task _appendCoreAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken) {
    // Get the next sequence number for this stream
    var lastSequence = await GetLastSequenceAsync(streamId, cancellationToken);
    var nextSequence = lastSequence + 1;

    // Serialize envelope.Payload to JsonElement for type-erased storage
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson).RootElement;

    // Create envelope metadata directly - EF Core will serialize via POCO mapping
    var metadata = new EnvelopeMetadata {
      MessageId = envelope.MessageId,
      Hops = envelope.Hops?.ToList() ?? []
    };

    var record = new EventStoreRecord {
      Id = envelope.MessageId.Value,  // Use MessageId from envelope as event_id (matches outbox message_id)
      StreamId = streamId,
      AggregateId = streamId,  // Backwards compatibility: AggregateId = StreamId
      // CLR full type name (no assembly, '+'-nested) via the shared formatter — identical across
      // EF Core and Dapper stores and matchable by IEventTypeRenameTool's clr_type_name UPDATE.
      AggregateType = TypeNameFormatter.FormatClrTypeName(typeof(TMessage)),
      Version = (int)nextSequence,  // Version for optimistic concurrency
      // Use centralized formatter for consistent type name format across all event stores
      // Format: "TypeName, AssemblyName" (medium form)
      // This matches wh_message_associations format and enables auto-checkpoint creation
      // Fuzzy matching in migration 006 handles AssemblyQualifiedName (long form) differences
      EventType = TypeNameFormatter.Format(typeof(TMessage)),
      // Full split (#13b4-2 / migration 077): the pointer is narrow — the body lives in wh_event_body.
      EventData = null,
      Metadata = null,
      CreatedAt = DateTime.UtcNow
    };
    var body = new EventBodyRecord {
      EventId = record.Id,
      EventData = eventData,
      Metadata = metadata,
    };

    await _context.Set<EventStoreRecord>().AddAsync(record, cancellationToken);
    await _context.Set<EventBodyRecord>().AddAsync(body, cancellationToken);

    try {
      await _context.SaveChangesAsync(cancellationToken);
    } catch (DbUpdateException ex) when (_isDuplicateKeyException(ex)) {
      // Concurrent append detected - optimistic concurrency failure
      throw new InvalidOperationException(
        $"Concurrent modification detected for stream {streamId} at sequence {nextSequence}. " +
        "Another process has already appended to this stream.",
        ex);
    }

    // NOTE: Inline perspective invocation removed - perspectives are now processed via PerspectiveWorker
    // using checkpoint-based processing for better reliability and scalability.
    // See: Stage 4 of perspective worker refactoring (2025-12-18)
  }

  /// <summary>
  /// Reads events from a stream with strong typing.
  /// Returns events in sequence order starting from the specified sequence number.
  /// Uses IAsyncEnumerable for efficient streaming of large event sequences.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:ReadAsync_WithExistingEvents_ReturnsEventsInSequenceOrderAsync</tests>
  [SuppressMessage("Major Bug", "S2955:Generic could be value type", Justification = "Event payload types in Whizbang are always reference types (records/classes implementing IEvent). Adding `class` constraint would break IEventStore.ReadAsync public contract.")]
  public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      long fromSequence,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    // Query events from the specified sequence onwards (body-aware: offloaded bodies join in,
    // reaped pointer-only rows are skipped).
    var query = _bodyAwareRows(_context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId && e.Version >= fromSequence)
      .OrderBy(e => e.Version))
      .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      if (record.EventData is null || record.Metadata is null) {
        continue;   // reaped ephemeral body — consumed, snapshot-covered history
      }
      // Deserialize the event payload using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.Value.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event at version {record.Version}");

      var hops = _restoreScopeInHops(record.Metadata, record.Scope);

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      // CRITICAL: Use record.Id (event_id column) as MessageId, NOT metadata.MessageId
      // This ensures the MessageId matches the event_id in wh_event_store for FK constraint integrity
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = MessageId.From(record.Id),
        Payload = eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Reads events from a stream with strong typing starting after a specific event ID.
  /// Returns events in UUIDv7 order (time-ordered) - no sequence numbers needed.
  /// Supports perspective checkpoint processing where last processed event ID is tracked.
  /// Uses IAsyncEnumerable for efficient streaming of large event sequences.
  /// </summary>
  [SuppressMessage("Major Bug", "S2955:Generic could be value type", Justification = "Event payload types in Whizbang are always reference types (records/classes implementing IEvent). Adding `class` constraint would break IEventStore.ReadAsync public contract.")]
  public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      Guid? fromEventId,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    // Query events from the specified event ID onwards (body-aware).
    // UUIDv7 is time-ordered, so we can order by Id directly
    var query = _bodyAwareRows(fromEventId == null
      ? _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.Id)
      : _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.Id))
      .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      if (record.EventData is null || record.Metadata is null) {
        continue;   // reaped ephemeral body — consumed, snapshot-covered history
      }
      // Deserialize the event payload using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.Value.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id}");

      var hops = _restoreScopeInHops(record.Metadata, record.Scope);

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      // CRITICAL: Use record.Id (event_id column) as MessageId, NOT metadata.MessageId
      // This ensures the MessageId matches the event_id in wh_event_store for FK constraint integrity
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = MessageId.From(record.Id),
        Payload = eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Slice 26.11 — resolves the commit_sequence stamped on a given event_id. Cheap
  /// single-row query used at snapshot creation time so the snapshot anchors on
  /// commit_sequence for deterministic rewind.
  /// </summary>
  public async Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) {
    var record = await _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.Id == eventId)
      .Select(e => new { e.CommitSequence })
      .FirstOrDefaultAsync(cancellationToken);
    return record?.CommitSequence;
  }

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// Uses the EventType column to determine which concrete type to deserialize to.
  /// </summary>
  public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId,
      Guid? fromEventId,
      IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    // Canonical normalized stored-EventType -> Type lookup, shared by every read path.
    var typeMap = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);

    // Query events from the specified event ID onwards. Slice 26: deterministic replay
    // order = commit_sequence ASC NULLS LAST, then event_id. Unstamped rows (NULL
    // commit_sequence) fall to the tail and still get processed; for the common case
    // where the stamper has caught up, replay order matches live-apply commit-completion
    // order regardless of UUIDv7 generation timing.
    var query = _bodyAwareRows(fromEventId == null
      ? _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.CommitSequence == null)
          .ThenBy(e => e.CommitSequence)
          .ThenBy(e => e.Id)
      : _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.CommitSequence == null)
          .ThenBy(e => e.CommitSequence)
          .ThenBy(e => e.Id))
      .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Look up the concrete type from the EventType column (canonical resolver — skip unknowns)
      if (!EventTypeMatchingHelper.TryResolveType(typeMap, record.EventType, out var concreteType)) {
        continue;
      }
      if (record.EventData is null || record.Metadata is null) {
        continue;   // reaped ephemeral body — consumed, snapshot-covered history
      }

      // Deserialize the event payload to the concrete type
      var eventDataJson = record.EventData.Value.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(concreteType);
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");

      var hops = _restoreScopeInHops(record.Metadata, record.Scope);

      // Reconstruct the message envelope with the polymorphic payload cast to IEvent.
      // LocalCommitSequence carries the wh_event_store.commit_sequence stamp so the
      // perspective runner's idempotency filter can compare against
      // metadata.CommitSequence — required to keep UUIDv7 generation-time inversions
      // from silently dropping late events (production forensic G5).
      var envelope = new MessageEnvelope<IEvent> {
        MessageId = record.Metadata.MessageId,
        Payload = (IEvent)eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
        LocalCommitSequence = record.CommitSequence
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Gets events between two checkpoint positions (exclusive start, inclusive end).
  /// Used by lifecycle receptors to load events that were just processed by a perspective.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_WithEventsInRange_ReturnsEventsBetweenCheckpointsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_NullAfterEventId_ReturnsFromStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_NoEventsInRange_ReturnsEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_MultipleEvents_ReturnsInUuidV7OrderAsync</tests>
  public async Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) {

    // Build query: after afterEventId (exclusive), up to upToEventId (inclusive)
    // Guid.Empty means "no upper bound" - read all events for the stream
    IQueryable<EventStoreRecord> query = _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId);

    // Apply upper bound only if upToEventId is not Guid.Empty
    if (upToEventId != Guid.Empty) {
      query = query.Where(e => e.Id <= upToEventId);
    }

    if (afterEventId != null) {
      query = query.Where(e => e.Id > afterEventId.Value);
    }

    // Order by UUID v7 (time-ordered); body-aware projection
    var records = await _bodyAwareRows(query.OrderBy(e => e.Id))
      .ToListAsync(cancellationToken);

    // Deserialize to message envelopes
    var envelopes = new List<MessageEnvelope<TMessage>>(records.Count);

    foreach (var record in records) {
      if (record.EventData is null || record.Metadata is null) {
        continue;   // reaped ephemeral body — consumed, snapshot-covered history
      }
      var eventDataJson = record.EventData.Value.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");

      var hops = _restoreScopeInHops(record.Metadata, record.Scope);

      envelopes.Add(new MessageEnvelope<TMessage> {
        MessageId = record.Metadata.MessageId,
        Payload = (TMessage)eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      });
    }

    return envelopes;
  }

  /// <summary>
  /// Gets events between two checkpoint positions, deserializing each event to its concrete type.
  /// Uses the EventType column to determine which concrete type to deserialize to.
  /// This is the polymorphic version of GetEventsBetweenAsync for perspectives that handle multiple event types.
  /// Used by lifecycle receptors to load events that were just processed by a perspective.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_WithMixedEventTypes_ReturnsAllEventsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_NullAfterEventId_ReturnsFromStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_NoEventsInRange_ReturnsEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_UnknownEventType_SkipsUnknownEventsAsync</tests>
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(eventTypes);
    return _getEventsBetweenPolymorphicCoreAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
  }

  private async Task<List<MessageEnvelope<IEvent>>> _getEventsBetweenPolymorphicCoreAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken) {
    // Build query: after afterEventId (exclusive), up to upToEventId (inclusive)
    // Guid.Empty means "no upper bound" - read all events for the stream
    IQueryable<EventStoreRecord> query = _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId);

    // Apply upper bound only if upToEventId is not Guid.Empty
    if (upToEventId != Guid.Empty) {
      query = query.Where(e => e.Id <= upToEventId);
    }

    if (afterEventId != null) {
      query = query.Where(e => e.Id > afterEventId.Value);
    }

    // Order by UUID v7 (time-ordered); body-aware projection
    var records = await _bodyAwareRows(query.OrderBy(e => e.Id))
      .ToListAsync(cancellationToken);

    // Canonical normalized stored-EventType -> Type lookup, shared by every read path.
    var typeLookup = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);

    // Deserialize to message envelopes with polymorphic payloads
    var envelopes = new List<MessageEnvelope<IEvent>>(records.Count);

    foreach (var record in records) {
      // Skip events that aren't in the perspective's list
      if (!EventTypeMatchingHelper.TryResolveType(typeLookup, record.EventType, out var concreteType)) {
        continue;
      }
      if (record.EventData is null || record.Metadata is null) {
        continue;   // reaped ephemeral body — consumed, snapshot-covered history
      }

      var eventDataJson = record.EventData.Value.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(concreteType);
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");

      var hops = _restoreScopeInHops(record.Metadata, record.Scope);

      envelopes.Add(new MessageEnvelope<IEvent> {
        MessageId = record.Metadata.MessageId,
        Payload = (IEvent)eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      });
    }

    return envelopes;
  }

  /// <summary>
  /// Gets the last (highest) sequence number for a stream.
  /// Returns -1 if the stream doesn't exist or is empty.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetLastSequenceAsync_WithEmptyStream_ReturnsMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetLastSequenceAsync_WithExistingEvents_ReturnsHighestSequenceAsync</tests>
  public async Task<long> GetLastSequenceAsync(
      Guid streamId,
      CancellationToken cancellationToken = default) {

    var lastSequence = await _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId)
      .MaxAsync(e => (long?)e.Version, cancellationToken);

    return lastSequence ?? -1;
  }

  /// <summary>
  /// Restores scope from the dedicated scope column into the first hop's ScopeDelta.
  /// Returns the (possibly modified) hops list.
  /// </summary>
  private static List<MessageHop> _restoreScopeInHops(EnvelopeMetadata metadata, PerspectiveScope? scope) {
    var hops = metadata.Hops.ToList();
    if (scope == null || hops.Count == 0 || hops[0].Scope != null) {
      return hops;
    }

    var scopeDelta = ScopeDelta.FromPerspectiveScope(scope);
    if (scopeDelta == null) {
      return hops;
    }

    hops[0] = hops[0] with { Scope = scopeDelta };
    return hops;
  }

  /// <summary>
  /// Checks if the exception is due to a duplicate key constraint violation.
  /// PostgreSQL uses error code 23505 for unique constraint violations.
  /// </summary>
  private static bool _isDuplicateKeyException(DbUpdateException ex) {
    // Check for PostgreSQL unique constraint violation
    // The error message typically contains "23505" or "duplicate key"
    return ex.InnerException?.Message.Contains("23505") == true ||
           ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DeserializeStreamEventsTests.cs:DeserializeStreamEvents_WhenEventDataIsCorrupt_LogsWarningAndKeepsGoodEventsAsync</tests>
  public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents,
      IReadOnlyList<Type> eventTypes) {
    if (streamEvents.Count == 0) {
      return [];
    }

    // Canonical normalized stored-EventType -> Type lookup, shared by every read path.
    var typeMap = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);

    var results = new List<MessageEnvelope<IEvent>>(streamEvents.Count);
    var deserializeFailures = 0;
    foreach (var raw in streamEvents) {
      MessageEnvelope<IEvent>? envelope;
      try {
        envelope = _tryBuildEnvelopeFromStreamEvent(raw, typeMap);
      } catch (Exception ex) {
        // A stored event failed to deserialize — corrupt data, a schema/type mismatch, or a
        // polymorphic payload whose "$type" was reordered out of first position by jsonb (see
        // JsonContextRegistry.CreateCombinedOptions). Don't let one bad event block the batch, but
        // NEVER swallow it silently: a silent skip here previously turned a TOTAL read failure into
        // "0 typed events" with no diagnostic, stalling perspective completion for days. Log the
        // first failure per batch in full (with the exception), then summarize the count.
        if (deserializeFailures == 0) {
          EFCoreEventStoreLog.DrainDeserializeFailure(_logger, ex, raw.EventId, raw.EventType);
        }
        deserializeFailures++;
        continue;
      }

      if (envelope is not null) {
        results.Add(envelope);
      }
    }

    if (deserializeFailures > 0) {
      EFCoreEventStoreLog.DrainDeserializeSkipped(_logger, deserializeFailures, streamEvents.Count);
    }

    return results;
  }

  /// <summary>
  /// Deserializes a single StreamEventData row into a MessageEnvelope. Returns null for an EXPECTED
  /// skip — an unknown/unresolvable event type or a null deserialization result. THROWS on a genuine
  /// deserialization failure (corrupt data, schema mismatch, reordered polymorphic discriminator);
  /// the caller (<see cref="DeserializeStreamEvents"/>) catches, logs, and continues so one bad event
  /// doesn't block the batch — but the failure is never silently swallowed.
  /// </summary>
  private MessageEnvelope<IEvent>? _tryBuildEnvelopeFromStreamEvent(
      StreamEventData raw,
      Dictionary<string, Type> typeMap) {
    if (!EventTypeMatchingHelper.TryResolveType(typeMap, raw.EventType, out var concreteType)) {
      return null;
    }

    var typeInfo = _jsonOptions.GetTypeInfo(concreteType);
    var eventData = JsonSerializer.Deserialize(raw.EventData, typeInfo);
    if (eventData is null) {
      return null;
    }

    var metadata = _deserializeMetadataIfPresent(raw.Metadata);
    var scope = _deserializeScopeIfPresent(raw.Scope);

    var hops = metadata?.Hops?.ToList() ?? [];
    // Restore scope into first hop (same pattern as _restoreScopeInHops)
    if (scope is not null && hops.Count > 0 && hops[0].Scope is null) {
      hops[0] = hops[0] with { Scope = ScopeDelta.FromPerspectiveScope(scope) };
    }

    return new MessageEnvelope<IEvent> {
      MessageId = metadata?.MessageId ?? new Whizbang.Core.ValueObjects.MessageId(raw.EventId),
      Payload = (IEvent)eventData,
      Hops = hops,
      DispatchContext = metadata?.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
      // Drain-mode parity with ReadPolymorphicAsync — carry the local commit_sequence stamp
      // through to the perspective runner so the idempotency filter can compare commit_sequence
      // when both sides have it (production forensic G5).
      LocalCommitSequence = raw.CommitSequence
    };
  }

  private EnvelopeMetadata? _deserializeMetadataIfPresent(string? raw) {
    if (string.IsNullOrEmpty(raw)) {
      return null;
    }
    var metadataTypeInfo = _jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata));
    return (EnvelopeMetadata?)JsonSerializer.Deserialize(raw, metadataTypeInfo);
  }

  private PerspectiveScope? _deserializeScopeIfPresent(string? raw) {
    if (string.IsNullOrEmpty(raw)) {
      return null;
    }
    var scopeTypeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveScope));
    return (PerspectiveScope?)JsonSerializer.Deserialize(raw, scopeTypeInfo);
  }
}

/// <summary>
/// Source-generated log messages for <see cref="EFCoreEventStore{TDbContext}"/>. Kept as a separate
/// non-generic class because the <c>[LoggerMessage]</c> source generator does not emit into generic
/// containing types.
/// </summary>
internal static partial class EFCoreEventStoreLog {
  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Failed to deserialize stream event {EventId} of type {EventType} during drain; skipping this event (first failure this batch).")]
  public static partial void DrainDeserializeFailure(ILogger logger, Exception ex, Guid eventId, string eventType);

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Drain deserialization skipped {SkippedCount} of {TotalCount} stream event(s) this batch; see the first-failure detail logged above.")]
  public static partial void DrainDeserializeSkipped(ILogger logger, int skippedCount, int totalCount);
}
