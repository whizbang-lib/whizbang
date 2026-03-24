using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IEventStore implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
public abstract class DapperEventStoreBase : IEventStore {
  protected IDbConnectionFactory ConnectionFactory { get; }
  protected IDbExecutor Executor { get; }
  protected JsonSerializerOptions JsonOptions { get; }

  protected DapperEventStoreBase(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerOptions jsonOptions
  ) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    ConnectionFactory = connectionFactory;
    Executor = executor;
    JsonOptions = jsonOptions;
  }

  /// <summary>
  /// Ensures the connection is open. Handles both pre-opened and closed connections.
  /// </summary>
  protected static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Gets the SQL command to append a new event to a stream.
  /// Parameters: @EventId (Guid), @StreamId (Guid), @SequenceNumber (long),
  ///             @EventType (string), @EventData (string), @Metadata (string),
  ///             @Scope (string), @CreatedAt (DateTimeOffset)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  protected abstract string GetAppendSql();

  /// <summary>
  /// Gets the SQL query to read events from a stream.
  /// Should return: EventData, Metadata, Scope (all as JSONB/string)
  /// Parameters: @StreamId (Guid), @FromSequence (long)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  protected abstract string GetReadSql();

  /// <summary>
  /// Gets the SQL query to get the last version number for a stream.
  /// Should return MAX(version) or -1 if stream doesn't exist.
  /// Parameters: @StreamId (Guid)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  protected abstract string GetLastSequenceSql();

  /// <summary>
  /// AOT-compatible append with explicit stream ID.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  public abstract Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default);

  /// <summary>
  /// Appends an event to the specified stream using a raw message.
  /// Derived classes must implement this to handle envelope lookup from IEnvelopeRegistry
  /// or create a minimal envelope if not found.
  /// </summary>
  public abstract Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;

  /// <summary>
  /// Reads events from a stream starting from a specific sequence number.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  public abstract IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    long fromSequence,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream starting after a specific event ID (UUIDv7-based).
  /// </summary>
  public abstract IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    Guid? fromEventId,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// </summary>
  public abstract IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
    Guid streamId,
    Guid? fromEventId,
    IReadOnlyList<Type> eventTypes,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Database-specific SQL for querying events between two checkpoint IDs (exclusive start, inclusive end).
  /// </summary>
  protected abstract string GetEventsBetweenSql();

  /// <inheritdoc />
  public async Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetEventsBetweenSql();
    var parameters = new {
      StreamId = streamId,
      AfterEventId = afterEventId,
      UpToEventId = upToEventId
    };

    var rows = await Executor.QueryAsync<EventRow>(connection, sql, parameters, cancellationToken: cancellationToken);
    var envelopes = new List<MessageEnvelope<TMessage>>();

    var eventTypeInfo = JsonOptions.GetTypeInfo(typeof(TMessage));

    foreach (var row in rows) {
      var eventData = JsonSerializer.Deserialize(row.EventData, eventTypeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event of type {row.EventType}");

      envelopes.Add(_buildEnvelope<TMessage>((TMessage)eventData, row));
    }

    return envelopes;
  }

  /// <inheritdoc />
  public async Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(eventTypes);

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetEventsBetweenSql();
    var parameters = new {
      StreamId = streamId,
      AfterEventId = afterEventId,
      UpToEventId = upToEventId
    };

    var rows = await Executor.QueryAsync<EventRow>(connection, sql, parameters, cancellationToken: cancellationToken);
    var envelopes = new List<MessageEnvelope<IEvent>>();

    // Build type lookup dictionary for fast O(1) lookups (AOT-compatible)
    var typeLookup = _buildTypeLookup(eventTypes);

    foreach (var row in rows) {
      var envelope = _tryDeserializePolymorphicRow(row, typeLookup);
      if (envelope != null) {
        envelopes.Add(envelope);
      }
    }

    return envelopes;
  }

  /// <summary>
  /// Gets the last sequence number for a stream. Returns -1 if stream doesn't exist.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  public async Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetLastSequenceSql();

    var lastSequence = await Executor.ExecuteScalarAsync<long?>(
      connection,
      sql,
      new { StreamId = streamId },
      cancellationToken: cancellationToken);

    return lastSequence ?? -1;
  }

  /// <summary>
  /// Normalizes an assembly-qualified type name to just the full type name (without assembly info).
  /// </summary>
  private static string _normalizeEventTypeName(string storedTypeName) {
    var commaIndex = storedTypeName.IndexOf(',');
    return commaIndex > 0 ? storedTypeName[..commaIndex].Trim() : storedTypeName;
  }

  /// <summary>
  /// Attempts to deserialize a single row into a polymorphic MessageEnvelope.
  /// Returns null if the event type is not in the type lookup (not relevant to this perspective).
  /// </summary>
  private MessageEnvelope<IEvent>? _tryDeserializePolymorphicRow(
      EventRow row,
      Dictionary<string, JsonTypeInfo> typeLookup) {
    var normalizedTypeName = _normalizeEventTypeName(row.EventType);

    // Skip events that aren't in the perspective's list
    if (!typeLookup.TryGetValue(normalizedTypeName, out var eventTypeInfo)) {
      return null;
    }

    var eventData = JsonSerializer.Deserialize(row.EventData, eventTypeInfo)
      ?? throw new InvalidOperationException($"Failed to deserialize event of type {row.EventType}");

    return _buildEnvelope<IEvent>((IEvent)eventData, row);
  }

  /// <summary>
  /// Builds a MessageEnvelope from deserialized event data and the raw row metadata/scope.
  /// </summary>
  private MessageEnvelope<TMessage> _buildEnvelope<TMessage>(TMessage payload, EventRow row) {
    var (messageId, hops) = _deserializeMetadataAndHops(row.Metadata, row.EventType);
    _restoreScopeFromJson(row.Scope, hops);

    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.From(messageId),
      Payload = payload,
      Hops = hops
    };
  }

  private (Guid messageId, List<MessageHop> hops) _deserializeMetadataAndHops(string metadataJson, string eventType) {
    var metadataDictTypeInfo = JsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
    var metadataDict = JsonSerializer.Deserialize(metadataJson, metadataDictTypeInfo) as Dictionary<string, JsonElement>
                       ?? throw new InvalidOperationException($"Failed to deserialize metadata for event type {eventType}");

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

  private void _restoreScopeFromJson(string? scopeJson, List<MessageHop> hops) {
    if (!_shouldRestoreScope(scopeJson, hops)) {
      return;
    }

    var scopeDict = _deserializeScopeDict(scopeJson!);
    if (scopeDict == null) {
      return;
    }

    _applyScopeToFirstHop(scopeDict, hops);
  }

  /// <summary>
  /// Determines whether scope restoration is needed: scope JSON must be present,
  /// hops must exist, and the first hop must not already have a scope.
  /// </summary>
  private static bool _shouldRestoreScope(string? scopeJson, List<MessageHop> hops) {
    return !string.IsNullOrEmpty(scopeJson) && hops.Count > 0 && hops[0].Scope == null;
  }

  /// <summary>
  /// Deserializes scope JSON into a dictionary, returning null if deserialization fails.
  /// </summary>
  private Dictionary<string, JsonElement?>? _deserializeScopeDict(string scopeJson) {
    var scopeDictTypeInfo = JsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement?>));
    return JsonSerializer.Deserialize(scopeJson, scopeDictTypeInfo) as Dictionary<string, JsonElement?>;
  }

  /// <summary>
  /// Extracts scope fields from the dictionary and applies them to the first hop if any values are present.
  /// </summary>
  private static void _applyScopeToFirstHop(Dictionary<string, JsonElement?> scopeDict, List<MessageHop> hops) {
    var scope = _buildPerspectiveScope(scopeDict);
    if (scope != null) {
      hops[0] = hops[0] with { Scope = ScopeDelta.FromPerspectiveScope(scope) };
    }
  }

  /// <summary>
  /// Builds a PerspectiveScope from scope dictionary values. Returns null if no scope values are present.
  /// Supports both short keys (t, u, c, o) and long keys (tenant_id, user_id).
  /// </summary>
  private static PerspectiveScope? _buildPerspectiveScope(Dictionary<string, JsonElement?> scopeDict) {
    var tenantId = _extractScopeValue(scopeDict, "t") ?? _extractScopeValue(scopeDict, "tenant_id");
    var userId = _extractScopeValue(scopeDict, "u") ?? _extractScopeValue(scopeDict, "user_id");
    var customerId = _extractScopeValue(scopeDict, "c");
    var organizationId = _extractScopeValue(scopeDict, "o");

    if (string.IsNullOrEmpty(tenantId) && string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(customerId) && string.IsNullOrEmpty(organizationId)) {
      return null;
    }

    return new PerspectiveScope {
      TenantId = tenantId,
      UserId = userId,
      CustomerId = customerId,
      OrganizationId = organizationId
    };
  }

  private static string? _extractScopeValue(Dictionary<string, JsonElement?> scopeDict, string key) {
    return scopeDict.TryGetValue(key, out var elem) && elem.HasValue && elem.Value.ValueKind != JsonValueKind.Null
      ? elem.Value.GetString()
      : null;
  }

  private Dictionary<string, JsonTypeInfo> _buildTypeLookup(IReadOnlyList<Type> eventTypes) {
    var typeLookup = new Dictionary<string, JsonTypeInfo>(eventTypes.Count);
    foreach (var type in eventTypes) {
      var typeInfo = JsonOptions.GetTypeInfo(type);
      typeLookup[type.FullName ?? type.Name] = typeInfo;
    }
    return typeLookup;
  }

  /// <summary>
  /// Internal row structure for Dapper mapping (3-column JSONB pattern).
  /// </summary>
  protected class EventRow {
    public string EventType { get; set; } = string.Empty;
    public string EventData { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public string? Scope { get; set; }
  }
}
