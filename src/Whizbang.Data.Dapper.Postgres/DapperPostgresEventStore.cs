// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IEventStore using Dapper with 3-column JSONB pattern.
/// Stream ID is inferred from event's [AggregateId] property.
/// Uses JsonbSizeValidator for C#-based size validation.
/// </summary>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStoreTests.cs</tests>
#pragma warning disable CS9113 // Primary constructor parameters are unread - retained for backward compatibility
public class DapperPostgresEventStore(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  JsonSerializerOptions jsonOptions,
  IJsonbPersistenceAdapter<IMessageEnvelope> adapter,
  JsonbSizeValidator sizeValidator,
  IPolicyEngine policyEngine,
  IPerspectiveInvoker? perspectiveInvoker,
  ILogger<DapperPostgresEventStore> logger,
  Whizbang.Core.IEphemeralModeResolver? ephemeralModeResolver = null
  ) : DapperEventStoreBase(connectionFactory, executor, jsonOptions) {
#pragma warning restore CS9113
  private readonly IJsonbPersistenceAdapter<IMessageEnvelope> _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
  private readonly JsonbSizeValidator _sizeValidator = sizeValidator ?? throw new ArgumentNullException(nameof(sizeValidator));
  private readonly IPolicyEngine _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
  // #1546: resolves [Ephemeral] declared on a type, so an appended event's kind is the one the dispatch path stamps.
  private readonly Whizbang.Core.IEphemeralModeResolver? _ephemeralModeResolver = ephemeralModeResolver;

  /// <summary>
  /// Appends an event to the specified stream (AOT-compatible).
  /// Stream ID is provided explicitly, avoiding reflection.
  /// Splits envelope into 3 JSONB columns and validates size. The database assigns the version under the
  /// stream's advisory lock (<c>wh_append_event</c>, migration 224), so concurrent appends to one stream
  /// serialize instead of colliding, and nothing is retried (#1555).
  /// </summary>
  /// <docs>fundamentals/events/event-store#appending-and-concurrency</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ParallelAppendsToOneStream_AllSucceedWithVersionsZeroToNMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_DuplicateEventId_FailsClearly_WithoutRetryingAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_WithNonUniqueViolationException_ShouldPropagateExceptionAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMixedStreamGuardTests.cs:AppendAsync_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync</tests>
  /// <exception cref="MixedStreamEventException">The event would make its stream hold both ephemeral and Sourced
  /// events (WHIZ133); nothing is stored.</exception>
  public override async Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    var jsonb = await _prepareJsonbAsync(envelope);

    await _insertEventAsync(streamId, envelope, jsonb, cancellationToken);
  }

  /// <inheritdoc />
  public override Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    var envelope = _createMinimalEnvelope(message);
    return AppendAsync(streamId, envelope, cancellationToken);
  }

  /// <summary>
  /// Reads events from a stream by stream ID (UUID) with strong typing.
  /// Reconstructs envelope from 3 JSONB columns.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ParallelAppendsToOneStream_AllSucceedWithVersionsZeroToNMinusOneAsync</tests>
  public override async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    long fromSequence,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var rows = await Executor.QueryAsync<EventRow>(
      connection,
      GetReadSql(),
      new {
        StreamId = streamId,
        FromSequence = fromSequence
      },
      cancellationToken: cancellationToken);

    foreach (var row in rows) {
      yield return _deserializeRow<TMessage>(row);
    }
  }

  /// <summary>
  /// Reads events from a stream starting after a specific event ID (UUIDv7-based).
  /// Uses UUIDv7 time ordering for natural event sequence without sequence numbers.
  /// Reconstructs envelope from 3 JSONB columns.
  /// </summary>
  public override async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    Guid? fromEventId,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = _getReadByEventIdSql(fromEventId);

    var rows = await Executor.QueryAsync<EventRow>(
      connection,
      sql,
      new {
        StreamId = streamId,
        FromEventId = fromEventId
      },
      cancellationToken: cancellationToken);

    foreach (var row in rows) {
      yield return _deserializeRow<TMessage>(row);
    }
  }

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// Uses the event_type column to determine which concrete type to deserialize to.
  /// </summary>
  /// <remarks>
  /// The stream is read in its own order: <c>version</c> ascending, <c>event_id</c> breaking ties, as the EF Core
  /// store reads it, and each envelope carries its version (<see cref="MessageEnvelope{T}.LocalStreamVersion"/>).
  /// An event id is minted before commit, so two producers can append in the opposite order to their ids (#1003);
  /// a commit sequence stamped after the fact can run backward against the versions (#1352). Either order would
  /// replay the stream backward.
  /// </remarks>
  /// <docs>fundamentals/events/event-store#read-order</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.EdgeCaseTests.cs:ReadPolymorphicAsync_IdsAndCommitsRunBackwardToVersions_ReadsInStreamOrderAsync</tests>
  public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
    Guid streamId,
    Guid? fromEventId,
    IReadOnlyList<Type> eventTypes,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var typeMap = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);
    var sql = _getReadInStreamOrderSql(fromEventId);

    var rows = await Executor.QueryAsync<EventRow>(
      connection,
      sql,
      new {
        StreamId = streamId,
        FromEventId = fromEventId
      },
      cancellationToken: cancellationToken);

    foreach (var row in rows) {
      yield return _deserializePolymorphicRow(row, typeMap, streamId);
    }
  }

  /// <summary>
  /// Determines whether an exception represents a PostgreSQL unique constraint violation (23505 error or message pattern match).
  /// </summary>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.UnitTests.cs:IsUniqueConstraintViolation_WithNonPostgresException_UniqueConstraintMessage_ShouldReturnTrueAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.UnitTests.cs:IsUniqueConstraintViolation_WithNonPostgresException_DuplicateKeyMessage_ShouldReturnTrueAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.UnitTests.cs:IsUniqueConstraintViolation_WithNonPostgresException_CaseInsensitive_ShouldReturnTrueAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.UnitTests.cs:IsUniqueConstraintViolation_WithNonPostgresException_DifferentMessage_ShouldReturnFalseAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.UnitTests.cs:IsUniqueConstraintViolation_WithArgumentException_DifferentMessage_ShouldReturnFalseAsync</tests>
  internal static bool IsUniqueConstraintViolation(Exception ex) {
    if (ex is Npgsql.PostgresException pgEx) {
      return pgEx.SqlState == "23505"; // unique_violation
    }
    return ex.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for appending an event: one call to <c>wh_append_event</c>
  /// (migration 224), which takes the stream's advisory lock, inserts the pointer row at the next version
  /// and the body into <c>wh_event_body</c>, and returns the version it assigned.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ParallelAppendsToOneStream_AllSucceedWithVersionsZeroToNMinusOneAsync</tests>
  protected override string GetAppendSql() => @"
    SELECT wh_append_event(
      @EventId, @StreamId, @AggregateId, @AggregateType, @EventType,
      @EventData::jsonb, @Metadata::jsonb, @Scope::jsonb, @CreatedAt, @Flags)";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for reading events from a stream by sequence number.
  /// </summary>
  /// <tests>No tests found</tests>
  protected override string GetReadSql() => @"
    SELECT
      es.event_type AS EventType,
      eb.event_data::text AS EventData,
      eb.metadata::text AS Metadata,
      es.scope::text AS Scope
    FROM wh_event_store es
    LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = @StreamId AND es.version >= @FromSequence
    ORDER BY es.version";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for querying events between two checkpoint IDs.
  /// Guid.Empty means "no upper bound" - read all events for the stream.
  /// </summary>
  protected override string GetEventsBetweenSql() => @"
    SELECT es.event_type AS EventType, eb.event_data::text AS EventData, eb.metadata::text AS Metadata, es.scope::text AS Scope
    FROM wh_event_store es
    LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = @StreamId
      AND (@UpToEventId = '00000000-0000-0000-0000-000000000000' OR es.event_id <= @UpToEventId)
      AND (@AfterEventId IS NULL OR es.event_id > @AfterEventId)
    ORDER BY es.event_id";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for retrieving the last sequence number in a stream.
  /// </summary>
  /// <tests>No tests found</tests>
  protected override string GetLastSequenceSql() => @"
    SELECT COALESCE(MAX(version), -1)
    FROM wh_event_store
    WHERE stream_id = @StreamId";

  /// <summary>
  /// Perspective row retention, the resurrection-on-wake history probe: does this stream hold any
  /// event ordered before the given id? An indexed EXISTS over the stream's pointer rows; uuid
  /// ordering is the store's own ordering (UUIDv7 ids), the same comparison the EF Core driver uses.
  /// </summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEventStoreHistoryProbeTests.cs</tests>
  public override async Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) {
    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var exists = await Executor.ExecuteScalarAsync<bool?>(
      connection,
      "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE stream_id = @StreamId AND event_id < @BeforeEventId)",
      new { StreamId = streamId, BeforeEventId = beforeEventId },
      cancellationToken: cancellationToken);
    return exists == true;
  }

  /// <summary>
  /// The perspective-aware history probe (issue #696): the stream's distinct pre-batch
  /// <c>event_type</c> values are read and matched in process through
  /// <see cref="EventTypeMatchingHelper"/>, the one strategy every read path shares, so a name a
  /// producer wrote in the decorated assembly-qualified form still matches. Mirrors the EF Core
  /// driver.
  /// </summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEventStoreHistoryProbeTests.cs</tests>
  public override async Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    if (eventTypes.Count == 0) {
      return false;
    }
    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var storedTypes = await Executor.QueryAsync<string>(
      connection,
      "SELECT DISTINCT event_type FROM wh_event_store WHERE stream_id = @StreamId AND event_id < @BeforeEventId",
      new { StreamId = streamId, BeforeEventId = beforeEventId },
      cancellationToken: cancellationToken);
    var lookup = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);
    foreach (var stored in storedTypes) {
      if (EventTypeMatchingHelper.TryResolveType(lookup, stored, out _)) {
        return true;
      }
    }
    return false;
  }

  // --- Private helper methods ---

  /// <summary>
  /// Prepares the JSONB persistence model by applying policies and size validation.
  /// </summary>
  private async Task<JsonbPersistenceModel> _prepareJsonbAsync<TMessage>(MessageEnvelope<TMessage> envelope) {
    var payload = envelope.Payload!;
    // Persistence policies here are matched on the message and envelope alone; no scoped services
    // take part in the decision, so none are offered.
    var policyCtx = new PolicyContext(payload, envelope, services: null);
    var policy = await _policyEngine.MatchAsync(policyCtx);

    var jsonb = _adapter.ToJsonb(envelope, policy);
    return _sizeValidator.Validate(jsonb, typeof(TMessage).Name, policy);
  }

  /// <summary>
  /// Inserts a single event into the event store.
  /// </summary>
  private async Task _insertEventAsync<TMessage>(
    Guid streamId,
    MessageEnvelope<TMessage> envelope,
    JsonbPersistenceModel jsonb,
    CancellationToken cancellationToken) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var parameters = _buildAppendParameters(streamId, envelope, jsonb, Whizbang.Data.Postgres.DirectAppendKind.Of(envelope.Payload, _ephemeralModeResolver));

    try {
      // #1546: the caller awaits the append, so a WHIZ133 refusal reaches it as MixedStreamEventException.
      await Whizbang.Data.Postgres.MixedStreamRefusal.RunAsync(() => Executor.ExecuteScalarAsync<int>(
        connection,
        GetAppendSql(),
        parameters,
        cancellationToken: cancellationToken));
    } catch (Exception ex) when (IsUniqueConstraintViolation(ex)) {
      // The version is assigned under the stream's lock and can no longer collide, so a unique violation is
      // the event id: this event is already stored. Retrying would only fail the same way.
      throw new InvalidOperationException(
        $"Event {envelope.MessageId.Value} was not appended to stream '{streamId}': an event with this id is already stored. " +
        "Versions are assigned by the database under the stream's lock, so this is not a concurrent append.",
        ex);
    }
  }

  /// <summary>
  /// Builds the parameter object for the append SQL statement.
  /// </summary>
  private static object _buildAppendParameters<TMessage>(
    Guid streamId,
    MessageEnvelope<TMessage> envelope,
    JsonbPersistenceModel jsonb,
    int flags) {

    return new {
      EventId = envelope.MessageId.Value,
      StreamId = streamId,
      AggregateId = streamId,
      // CLR full type name (no assembly, '+'-nested) via the shared formatter — identical to the
      // EF Core store and matchable by IEventTypeRenameTool's clr_type_name UPDATE. Previously this
      // wrote the bare simple name (typeof(TMessage).Name), diverging from EF Core and the rename tool.
      AggregateType = TypeNameFormatter.FormatClrTypeName(typeof(TMessage)),
      EventType = TypeNameFormatter.Format(typeof(TMessage)),
      EventData = jsonb.DataJson,
      Metadata = jsonb.MetadataJson,
      Scope = jsonb.ScopeJson,
      CreatedAt = DateTimeOffset.UtcNow,
      // #1546: the event's kind, stored on the row and checked against its stream's (WHIZ133).
      Flags = flags
    };
  }

  /// <summary>
  /// Creates a minimal envelope for a raw message.
  /// </summary>
  private static MessageEnvelope<TMessage> _createMinimalEnvelope<TMessage>(TMessage message) {
    return new MessageEnvelope<TMessage> {
      Priority = Whizbang.Core.Priority.PriorityContext.CurrentParent,   // priority step 1: declared from the handling in progress, undeclared outside one
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
  }

  /// <summary>
  /// Returns the appropriate SQL for reading events by event ID, with or without a starting event ID filter.
  /// </summary>
  private static string _getReadByEventIdSql(Guid? fromEventId) {
    if (fromEventId == null) {
      return @"SELECT es.event_type AS EventType,
                 eb.event_data::text AS EventData,
                 eb.metadata::text AS Metadata,
                 es.scope::text AS Scope
          FROM wh_event_store es
          LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
          WHERE es.stream_id = @StreamId
          ORDER BY es.event_id";
    }

    return @"SELECT es.event_type AS EventType,
                 eb.event_data::text AS EventData,
                 eb.metadata::text AS Metadata,
                 es.scope::text AS Scope
          FROM wh_event_store es
          LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
          WHERE es.stream_id = @StreamId AND es.event_id > @FromEventId
          ORDER BY es.event_id";
  }

  /// <summary>
  /// The SQL that reads a stream in its own order (<c>version</c>, then <c>event_id</c>), after a starting event ID
  /// when one is given: the order a replay applies the stream in. Commit order matched it only for rows stamped as
  /// they were written; rows stamped after the fact run backward against their versions (#1352).
  /// </summary>
  private static string _getReadInStreamOrderSql(Guid? fromEventId) =>
    fromEventId is null
      ? @"SELECT es.event_type AS EventType,
                 eb.event_data::text AS EventData,
                 eb.metadata::text AS Metadata,
                 es.scope::text AS Scope,
                 es.version AS Version
          FROM wh_event_store es
          LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
          WHERE es.stream_id = @StreamId
          ORDER BY es.version, es.event_id"
      : @"SELECT es.event_type AS EventType,
                 eb.event_data::text AS EventData,
                 eb.metadata::text AS Metadata,
                 es.scope::text AS Scope,
                 es.version AS Version
          FROM wh_event_store es
          LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
          WHERE es.stream_id = @StreamId AND es.event_id > @FromEventId
          ORDER BY es.version, es.event_id";

  /// <summary>
  /// Deserializes an EventRow into a typed MessageEnvelope using the JSONB adapter.
  /// </summary>
  private MessageEnvelope<TMessage> _deserializeRow<TMessage>(EventRow row) {
    var jsonb = new JsonbPersistenceModel {
      DataJson = row.EventData,
      MetadataJson = row.Metadata,
      ScopeJson = row.Scope
    };

    return _adapter.FromJsonb<TMessage>(jsonb);
  }

  /// <summary>
  /// Deserializes an EventRow into a polymorphic MessageEnvelope using type resolution from the type map.
  /// </summary>
  private MessageEnvelope<IEvent> _deserializePolymorphicRow(
    EventRow row,
    Dictionary<string, Type> typeMap,
    Guid streamId) {

    var concreteType = _resolveEventType(row.EventType, typeMap, streamId);
    var jsonb = _rowToJsonbModel(row);
    var eventData = _deserializeEventData(jsonb, concreteType);

    var (messageId, hops) = _deserializeMetadataAndHops(jsonb);
    StoredScope.RestoreInto(hops, StoredScope.Read(jsonb.ScopeJson, JsonOptions));

    var envelope = _buildPolymorphicEnvelope(eventData, messageId, hops);
    envelope.LocalStreamVersion = row.Version;
    return envelope;
  }

  /// <summary>
  /// Resolves a stored event type string to its CLR Type using the canonical resolver.
  /// Unlike the EFCore/base read paths (which skip unknown types), this store throws on an
  /// unrecognized type — preserving its historical fail-loud contract for direct stream reads.
  /// </summary>
  private static Type _resolveEventType(
    string eventType,
    Dictionary<string, Type> typeMap,
    Guid streamId) {

    if (EventTypeMatchingHelper.TryResolveType(typeMap, eventType, out var concreteType)) {
      return concreteType;
    }

    throw new InvalidOperationException(
      $"Event type '{eventType}' in stream {streamId} is not in the provided event types list. " +
      $"Available types: {string.Join(", ", typeMap.Keys)}");
  }

  /// <summary>
  /// Converts an EventRow to a JsonbPersistenceModel.
  /// </summary>
  private static JsonbPersistenceModel _rowToJsonbModel(EventRow row) {
    return new JsonbPersistenceModel {
      DataJson = row.EventData,
      MetadataJson = row.Metadata,
      ScopeJson = row.Scope
    };
  }

  /// <summary>
  /// Deserializes event data from JSONB using the concrete type's JsonTypeInfo.
  /// </summary>
  private object _deserializeEventData(JsonbPersistenceModel jsonb, Type concreteType) {
    var typeInfo = JsonOptions.GetTypeInfo(concreteType);

    return JsonSerializer.Deserialize(jsonb.DataJson, typeInfo)
      ?? throw new InvalidOperationException($"Failed to deserialize event of type {TypeNameFormatter.DisplayName(concreteType)}");
  }

  /// <summary>
  /// Builds a polymorphic MessageEnvelope from deserialized event data.
  /// </summary>
  private static MessageEnvelope<IEvent> _buildPolymorphicEnvelope(
    object eventData,
    Guid messageId,
    List<MessageHop> hops) {

    if (eventData is not IEvent eventPayload) {
      throw new InvalidOperationException(
        $"Deserialized event of type {TypeNameFormatter.DisplayName(eventData.GetType())} does not implement IEvent.");
    }

    return new MessageEnvelope<IEvent> {
      MessageId = MessageId.From(messageId),
      Payload = eventPayload,
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
    };
  }

  private (Guid messageId, List<MessageHop> hops) _deserializeMetadataAndHops(JsonbPersistenceModel jsonb) {
    var metadataDictTypeInfo = JsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
    var metadataDict = JsonSerializer.Deserialize(jsonb.MetadataJson, metadataDictTypeInfo) as Dictionary<string, JsonElement>
                       ?? throw new InvalidOperationException("Failed to deserialize metadata JSON");

    var messageId = metadataDict.TryGetValue("message_id", out var msgIdElem)
      ? Guid.Parse(msgIdElem.GetString()!)
      : throw new InvalidOperationException("message_id not found in metadata");

    List<MessageHop> hops;
    if (metadataDict.TryGetValue("hops", out var hopsElem)) {
      var hopsTypeInfo = JsonOptions.GetTypeInfo(typeof(List<MessageHop>));
      hops = JsonSerializer.Deserialize(hopsElem.GetRawText(), hopsTypeInfo) as List<MessageHop> ?? [];
    } else {
      hops = [];
    }

    return (messageId, hops);
  }
}
