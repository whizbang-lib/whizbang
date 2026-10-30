// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
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
  ILogger<EFCoreEventStore<TDbContext>>? logger = null,
  Whizbang.Core.IEphemeralModeResolver? ephemeralModeResolver = null) : IEventStore
  where TDbContext : DbContext {

  private const string APPEND_EVENT_FUNCTION = "wh_append_event";

  private readonly TDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? EFCoreJsonContext.CreateCombinedOptions();
  // Null-object default so a drain deserialize failure is ALWAYS logged: DI supplies a real logger in
  // production; the NullLogger fallback only applies to manual construction / a log-free host.
  private readonly ILogger<EFCoreEventStore<TDbContext>> _logger = logger ?? NullLogger<EFCoreEventStore<TDbContext>>.Instance;
  // #1546: resolves [Ephemeral] declared on a type, so an appended event's kind is the one the dispatch path stamps.
  private readonly Whizbang.Core.IEphemeralModeResolver? _ephemeralModeResolver = ephemeralModeResolver;

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
    public bool IsFirstInStream { get; init; }
    public long? CommitSequence { get; init; }
    public Guid? OriginServiceId { get; init; }
    public long? OriginCommitSequence { get; init; }
    public JsonElement? EventData { get; init; }
    public EnvelopeMetadata? Metadata { get; init; }
    public PerspectiveScope? Scope { get; init; }
  }

  /// <summary>
  /// Projects a (filtered, ordered) pointer query into body-aware rows via LEFT JOIN on
  /// <c>wh_event_body</c>. Apply Where/OrderBy on the pointer query BEFORE calling this.
  /// </summary>
  // IDE0031 (use null propagation) is off across this projection: it is an EF Core expression
  // tree, where `b?.Metadata` is a compile error (CS8072, "an expression tree lambda may not
  // contain a null propagating operator"). The explicit null check is the only legal spelling,
  // and `dotnet format style` rewrites it to the illegal one on every run without this.
#pragma warning disable IDE0031
  private IQueryable<EventRow> _bodyAwareRows(IQueryable<EventStoreRecord> pointers) =>
    from e in pointers
    join body in _context.Set<EventBodyRecord>().AsNoTracking()
      on e.Id equals body.EventId into bodies
    from b in bodies.DefaultIfEmpty()
    select new EventRow {
      Id = e.Id,
      EventType = e.EventType,
      Version = e.Version,
      // The first event in its stream (#1878): version 0, or version 1 when the stream holds no version 0. The
      // lookup is a probe of the (stream_id, version) unique index, made for version-1 rows only.
      IsFirstInStream = e.Version == 0
        || (e.Version == 1 && !_context.Set<EventStoreRecord>().Any(z => z.StreamId == e.StreamId && z.Version == 0)),
      CommitSequence = e.CommitSequence,
      OriginServiceId = e.OriginServiceId,
      OriginCommitSequence = e.OriginCommitSequence,
      Scope = e.Scope,
      // #13b4-3 (078): the inline columns are dropped — the body table IS the body. A missing body
      // row (reaped ephemeral) surfaces as NULL and the caller skips it.
      EventData = b != null ? (JsonElement?)b.EventData : null,
      Metadata = b != null ? b.Metadata : null,
    };
#pragma warning restore IDE0031

  /// <summary>
  /// Appends an event to the specified stream.
  /// The database assigns the next version under the stream's advisory lock (<c>wh_append_event</c>,
  /// migration 224), so concurrent appends to one stream serialize rather than collide (#1555).
  /// Pending changes tracked by the context are saved first, as the append always flushed them.
  /// </summary>
  /// <docs>fundamentals/events/event-store#appending-and-concurrency</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithValidEnvelope_AppendsEventToStreamAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithMultipleEvents_AssignsSequentialSequenceNumbersAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendMixedStreamGuardTests.cs:DirectAppend_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
  /// <exception cref="MixedStreamEventException">The event would make its stream hold both ephemeral and Sourced
  /// events (WHIZ133); nothing is stored.</exception>
  /// <exception cref="StreamSealedException">The stream is sealed (#1733); nothing is stored.</exception>
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
    // Serialize envelope.Payload for type-erased storage
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);

    // Envelope metadata is bound as the same POCO EF Core's jsonb mapping wrote, so the stored
    // document is produced by the same serializer as before (the data source's JSON options).
    var metadata = new EnvelopeMetadata {
      MessageId = envelope.MessageId,
      Hops = envelope.Hops?.ToList() ?? []
    };

    // The append used to ride SaveChangesAsync, which also flushed whatever else the shared, scoped
    // context was tracking. Callers may depend on that, so pending changes are still saved first.
    if (_context.ChangeTracker.HasChanges()) {
      await _context.SaveChangesAsync(cancellationToken);
    }

    var eventId = envelope.MessageId.Value;  // event_id = the envelope's MessageId (matches outbox message_id)
    var functionName = EFCoreWorkCoordinator<TDbContext>.BuildSchemaQualifiedName(
      EFCoreWorkCoordinator<TDbContext>.ResolveSchema(_context.Model, typeof(EventStoreRecord), logger: null),
      APPEND_EVENT_FUNCTION);

    // The database assigns the version under the stream's advisory lock, the lock the emit chain
    // takes (migration 224, #1555), so concurrent appends to one stream serialize instead of
    // colliding on (stream_id, version). The command runs on the context's connection and in its
    // current transaction, so an ambient transaction covers the append.
    await _context.Database.OpenConnectionAsync(cancellationToken);
    try {
      await using var command = _context.Database.GetDbConnection().CreateCommand();
      command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
#pragma warning disable S2077 // The function name is schema-qualified from the EF model, never from input.
      command.CommandText = $"SELECT {functionName}(@p_event_id, @p_stream_id, @p_aggregate_id, @p_aggregate_type, @p_event_type, @p_event_data, @p_metadata, @p_scope, @p_created_at, @p_flags)";
#pragma warning restore S2077
      command.Parameters.Add(new NpgsqlParameter("p_event_id", NpgsqlDbType.Uuid) { Value = eventId });
      command.Parameters.Add(new NpgsqlParameter("p_stream_id", NpgsqlDbType.Uuid) { Value = streamId });
      // Backwards compatibility: AggregateId = StreamId
      command.Parameters.Add(new NpgsqlParameter("p_aggregate_id", NpgsqlDbType.Uuid) { Value = streamId });
      // CLR full type name (no assembly, '+'-nested) via the shared formatter: identical across
      // EF Core and Dapper stores and matchable by IEventTypeRenameTool's clr_type_name UPDATE.
      command.Parameters.Add(new NpgsqlParameter("p_aggregate_type", NpgsqlDbType.Text) { Value = TypeNameFormatter.FormatClrTypeName(typeof(TMessage)) });
      // "TypeName, AssemblyName" (medium form), matching wh_message_associations.
      command.Parameters.Add(new NpgsqlParameter("p_event_type", NpgsqlDbType.Text) { Value = TypeNameFormatter.Format(typeof(TMessage)) });
      command.Parameters.Add(new NpgsqlParameter("p_event_data", NpgsqlDbType.Jsonb) { Value = eventDataJson });
      command.Parameters.Add(new NpgsqlParameter("p_metadata", NpgsqlDbType.Jsonb) { Value = metadata });
      // This store never wrote the scope column; the scope travels in the hops.
      command.Parameters.Add(new NpgsqlParameter("p_scope", NpgsqlDbType.Jsonb) { Value = DBNull.Value });
      command.Parameters.Add(new NpgsqlParameter("p_created_at", NpgsqlDbType.TimestampTz) { Value = DateTime.UtcNow });
      // #1546: the event's kind, stored on the row and checked against its stream's (WHIZ133).
      command.Parameters.Add(new NpgsqlParameter("p_flags", NpgsqlDbType.Integer) { Value = Whizbang.Data.Postgres.DirectAppendKind.Of(envelope.Payload, _ephemeralModeResolver) });
      // The caller awaits the append, so a refusal reaches it as MixedStreamEventException, or as StreamSealedException
      // for a sealed stream (#1733), with nothing stored.
      await Whizbang.Data.Postgres.StreamSealedRefusal.RunAsync(() =>
        Whizbang.Data.Postgres.MixedStreamRefusal.RunAsync(() => command.ExecuteScalarAsync(cancellationToken)));
    } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) {
      // The version can no longer collide, so a unique violation is the event id: this event is already stored.
      throw new InvalidOperationException(
        $"Event {eventId} was not appended to stream {streamId}: it violates unique constraint '{ex.ConstraintName}'. " +
        "Versions are assigned by the database under the stream's lock, so this is not a concurrent append; " +
        "an event with this id is already stored.",
        ex);
    } finally {
      await _context.Database.CloseConnectionAsync();
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
      var eventData = (TMessage)_deserializePayload(record, record.EventData.Value, typeof(TMessage));

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
      var eventData = (TMessage)_deserializePayload(record, record.EventData.Value, typeof(TMessage));

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
  /// Perspective row retention — the resurrection-on-wake history probe: does this stream hold
  /// any event ordered before the given id? An indexed EXISTS over the stream's pointer rows;
  /// UUIDv7 ordering is safe at this granularity (a reaped row's history predates the waking
  /// batch by at least the row TTL — days, far beyond any concurrent-emission id inversion).
  /// </summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreHistoryProbeSqlTests.cs</tests>
  public async Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) {
    return await _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .AnyAsync(e => e.StreamId == streamId && e.Id.CompareTo(beforeEventId) < 0, cancellationToken);
  }

  /// <summary>
  /// The perspective-aware history probe (issue #696): does this stream hold an event ordered
  /// before the given id whose stored type is one the perspective folds? The stream's distinct
  /// pre-batch <c>event_type</c> values are read (an indexed range over the stream's pointer rows;
  /// a stream carries few distinct types) and matched in process through
  /// <see cref="EventTypeMatchingHelper"/>, the one strategy every read path shares, so a name a
  /// producer wrote in the decorated assembly-qualified form still matches.
  /// </summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreHistoryProbeSqlTests.cs</tests>
  public async Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    if (eventTypes.Count == 0) {
      return false;
    }
    var storedTypes = await _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId && e.Id.CompareTo(beforeEventId) < 0)
      .Select(e => e.EventType)
      .Distinct()
      .ToListAsync(cancellationToken);
    return _anyStoredTypeIsHandled(storedTypes, eventTypes);
  }

  private static bool _anyStoredTypeIsHandled(IEnumerable<string> storedTypes, IReadOnlyList<Type> eventTypes) {
    var lookup = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);
    foreach (var stored in storedTypes) {
      if (EventTypeMatchingHelper.TryResolveType(lookup, stored, out _)) {
        return true;
      }
    }
    return false;
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

    // Query events from the specified event ID onwards, in the stream's own order: its version, the position the
    // local store appended each event at (#1352). Slice 26 read in commit_sequence order, which matches the version
    // only when every row was stamped as it was written; rows stamped after the fact carry sequences that run
    // backward against their versions, and a replay in that order folded the stream's last event first.
    // ReplayEventOrder makes the final call from the envelope (LocalStreamVersion); reading in the same order keeps
    // the two agreeing for any caller that takes the read as it comes.
    var query = _bodyAwareRows(fromEventId == null
      ? _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.Version)
          .ThenBy(e => e.Id)
      : _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.Version)
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
      var eventData = _deserializePayload(record, record.EventData.Value, concreteType);

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
        LocalCommitSequence = record.CommitSequence,
        // The stream's own order (#1352): replay folds by version, because a commit sequence stamped after the fact
        // can run backward against the order the stream was written in.
        LocalStreamVersion = record.Version,
        // Decided by the query from the event's position, whatever the read starts after (#1878).
        IsFirstInLocalStream = record.IsFirstInStream,
        // Origin identity rides the replay read: the rewind's origin-order slotting keys on it
        // (a reconciled straggler's local sequence is arrival-fresh, but its origin sequence is
        // its true position in the stream). Dropping it demotes single-origin streams to
        // local-commit order and re-applies stragglers last.
        SourceServiceId = record.OriginServiceId ?? Guid.Empty,
        SourceCommitSequence = record.OriginCommitSequence ?? 0,
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
      var eventData = (TMessage)_deserializePayload(record, record.EventData.Value, typeof(TMessage));

      var hops = _restoreScopeInHops(record.Metadata, record.Scope);

      envelopes.Add(new MessageEnvelope<TMessage> {
        MessageId = record.Metadata.MessageId,
        Payload = eventData,
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

      var eventData = _deserializePayload(record, record.EventData.Value, concreteType);

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
  /// <summary>
  /// Deserializes one stored event payload. A payload stored as JSON null holds no event, and handing a
  /// reader a null in its place would fail later and further from the cause, so it is refused here, for
  /// every read path, naming the row.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs</tests>
  private object _deserializePayload(EventRow record, JsonElement eventData, Type payloadType) {
    // The row's payload column was already parsed once into a JsonElement when the row was materialized;
    // this is the second parse, from its raw text into the event's own type.
    var payload = JsonSerializer.Deserialize(eventData.GetRawText(), _jsonOptions.GetTypeInfo(payloadType))
      ?? throw new InvalidOperationException(
        $"Failed to deserialize event ID {record.Id} at version {record.Version} of type {record.EventType}: the stored payload is JSON null.");
    MessageParseDiagnostics.Record(record.Id, MessageParseSource.EventStoreRead, MessageParseKind.Payload);
    return payload;
  }

  private static List<MessageHop> _restoreScopeInHops(EnvelopeMetadata? metadata, PerspectiveScope? scope) {
    var hops = metadata?.Hops?.ToList() ?? [];
    if (scope == null || (hops.Count > 0 && hops[0].Scope != null)) {
      return hops;
    }

    var scopeDelta = ScopeDelta.FromPerspectiveScope(scope);
    if (scopeDelta == null) {
      return hops;
    }

    if (hops.Count == 0) {
      // No hops to hang it on. That is the normal shape when an event is read back from the store:
      // the row keeps its scope in a column but carries no envelope metadata, so there are no hops
      // to restore into.
      //
      // Returning early here dropped a scope that had already been read and deserialized.
      // GetCurrentScope() walks hops, so it returned null, and any perspective requiring a security
      // context threw on every replayed event — a retry that could never succeed, ten times per
      // event, then a permanent park.
      //
      // Synthesizing a hop restores exactly what the store persisted. No authority is invented: an
      // event with no stored scope still yields none, because scope == null returns above.
      hops.Add(new MessageHop {
        ServiceInstance = _replayInstance(),
        Scope = scopeDelta,
      });
      return hops;
    }

    hops[0] = hops[0] with { Scope = scopeDelta };
    return hops;
  }

  /// <summary>
  /// Identity stamped on a hop synthesized while reading an event back from the store. The
  /// originating instance is not recorded on the row, and inventing one would misattribute the hop;
  /// this names the reader, which is what actually produced it.
  /// </summary>
  private static ServiceInstanceInfo _replayInstance() => new() {
    InstanceId = Guid.Empty,
    ServiceName = "replay",
    HostName = Environment.MachineName,
    ProcessId = Environment.ProcessId,
  };

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
    MessageParseDiagnostics.Record(raw.EventId, MessageParseSource.EventStoreRead, MessageParseKind.Payload);
    if (eventData is null) {
      return null;
    }

    var metadata = _deserializeMetadataIfPresent(raw.EventId, raw.Metadata);
    var scope = _deserializeScopeIfPresent(raw.EventId, raw.Scope);

    // Same restore path the read methods use, so drain mode and ReadPolymorphicAsync cannot
    // drift apart on scope handling again — they previously did, and the drain-mode copy was
    // fixed while the shared one kept dropping scope for events with no hops.
    var hops = _restoreScopeInHops(metadata, scope);
    return new MessageEnvelope<IEvent> {
      MessageId = metadata?.MessageId ?? new Whizbang.Core.ValueObjects.MessageId(raw.EventId),
      Payload = (IEvent)eventData,
      Hops = hops,
      DispatchContext = metadata?.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
      // Drain-mode parity with ReadPolymorphicAsync — carry the local commit_sequence stamp
      // through to the perspective runner so the idempotency filter can compare commit_sequence
      // when both sides have it (production forensic G5).
      LocalCommitSequence = raw.CommitSequence,
      // The drain query decided it from the event's position (264, #1878).
      IsFirstInLocalStream = raw.IsFirstInStream,
    };
  }

  private EnvelopeMetadata? _deserializeMetadataIfPresent(Guid eventId, string? raw) {
    if (string.IsNullOrEmpty(raw)) {
      return null;
    }
    MessageParseDiagnostics.Record(eventId, MessageParseSource.EventStoreRead, MessageParseKind.Metadata);
    var metadataTypeInfo = _jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata));
    return (EnvelopeMetadata?)JsonSerializer.Deserialize(raw, metadataTypeInfo);
  }

  private PerspectiveScope? _deserializeScopeIfPresent(Guid eventId, string? raw) {
    if (string.IsNullOrEmpty(raw)) {
      return null;
    }
    MessageParseDiagnostics.Record(eventId, MessageParseSource.EventStoreRead, MessageParseKind.Scope);
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
