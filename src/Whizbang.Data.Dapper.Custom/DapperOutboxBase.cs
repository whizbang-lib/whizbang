using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IOutbox implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// Uses JSONB adapter for envelope serialization.
/// Supports both immediate processing (with instanceId) and polling-based (without instanceId).
/// </summary>
public abstract class DapperOutboxBase : IOutbox {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;
  protected readonly IJsonbPersistenceAdapter<IMessageEnvelope> _envelopeAdapter;
  protected readonly Guid? _instanceId;
  protected readonly int _leaseSeconds;

  /// <summary>
  /// Creates a new DapperOutboxBase instance.
  /// </summary>
  /// <param name="connectionFactory">Database connection factory</param>
  /// <param name="executor">Database command executor</param>
  /// <param name="envelopeAdapter">JSONB persistence adapter for envelopes</param>
  /// <param name="instanceId">Service instance ID for lease-based coordination (optional, enables immediate processing)</param>
  /// <param name="leaseSeconds">Lease duration in seconds (default 300 = 5 minutes)</param>
  protected DapperOutboxBase(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    IJsonbPersistenceAdapter<IMessageEnvelope> envelopeAdapter,
    Guid? instanceId = null,
    int leaseSeconds = 300) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);
    ArgumentNullException.ThrowIfNull(envelopeAdapter);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _envelopeAdapter = envelopeAdapter;
    _instanceId = instanceId;
    _leaseSeconds = leaseSeconds;
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
  /// Gets the SQL command to store a new outbox message using JSONB pattern.
  /// Parameters: @MessageId (Guid), @Destination (string), @EventType (string),
  ///             @EventData (string), @Metadata (string), @Scope (string, nullable),
  ///             @Status (string), @Attempts (int), @CreatedAt (DateTimeOffset),
  ///             @InstanceId (Guid, nullable), @LeaseExpiry (DateTimeOffset, nullable)
  /// </summary>
  protected abstract string GetStoreSql();

  /// <summary>
  /// Gets the SQL query to retrieve pending outbox messages using JSONB pattern.
  /// Should return: MessageId, Destination, EventType, EventData, Metadata, Scope, CreatedAt
  /// Parameters: @BatchSize (int)
  /// </summary>
  protected abstract string GetPendingSql();

  /// <summary>
  /// Gets the SQL command to mark a message as published.
  /// Parameters: @MessageId (Guid), @PublishedAt (DateTimeOffset)
  /// </summary>
  protected abstract string GetMarkPublishedSql();

  public async Task<OutboxMessage> StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string destination, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    // Convert envelope to JSONB format using adapter
    var jsonbModel = _envelopeAdapter.ToJsonb(envelope);

    // Get event type from payload
    var eventType = typeof(TMessage).FullName ?? throw new InvalidOperationException("Event type has no FullName");

    var now = DateTimeOffset.UtcNow;
    var sql = GetStoreSql();

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = envelope.MessageId.Value,
        Destination = destination,
        EventType = eventType,
        EventData = jsonbModel.DataJson,
        Metadata = jsonbModel.MetadataJson,
        Scope = jsonbModel.ScopeJson,
        Status = _instanceId.HasValue ? "Publishing" : "Pending",
        Attempts = 0,
        CreatedAt = now,
        InstanceId = _instanceId,
        LeaseExpiry = _instanceId.HasValue ? now.AddSeconds(_leaseSeconds) : (DateTimeOffset?)null
      },
      cancellationToken: cancellationToken);

    return new OutboxMessage(
      envelope.MessageId,
      destination,
      eventType,
      jsonbModel.DataJson,
      jsonbModel.MetadataJson,
      jsonbModel.ScopeJson,
      now
    );
  }

  public async Task<OutboxMessage> StoreAsync(IMessageEnvelope envelope, string destination, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    // Convert envelope to JSONB format using adapter (works with IMessageEnvelope)
    var jsonbModel = _envelopeAdapter.ToJsonb(envelope);

    // Get event type from envelope runtime type
    var eventType = envelope.GetType().GenericTypeArguments[0].FullName
      ?? throw new InvalidOperationException("Event type has no FullName");

    var now = DateTimeOffset.UtcNow;
    var sql = GetStoreSql();

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = envelope.MessageId.Value,
        Destination = destination,
        EventType = eventType,
        EventData = jsonbModel.DataJson,
        Metadata = jsonbModel.MetadataJson,
        Scope = jsonbModel.ScopeJson,
        Status = _instanceId.HasValue ? "Publishing" : "Pending",
        Attempts = 0,
        CreatedAt = now,
        InstanceId = _instanceId,
        LeaseExpiry = _instanceId.HasValue ? now.AddSeconds(_leaseSeconds) : (DateTimeOffset?)null
      },
      cancellationToken: cancellationToken);

    return new OutboxMessage(
      envelope.MessageId,
      destination,
      eventType,
      jsonbModel.DataJson,
      jsonbModel.MetadataJson,
      jsonbModel.ScopeJson,
      now
    );
  }

  public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetPendingSql();

    var rows = await _executor.QueryAsync<OutboxRow>(
      connection,
      sql,
      new { BatchSize = batchSize },
      cancellationToken: cancellationToken);

    return [.. rows.Select(r => new OutboxMessage(
      MessageId.From(r.MessageId),
      r.Destination,
      r.EventType,
      r.EventData,
      r.Metadata,
      r.Scope,
      r.CreatedAt
    ))];
  }

  public async Task MarkPublishedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetMarkPublishedSql();

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = messageId.Value,
        PublishedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// Maps to JSONB-based outbox schema.
  /// </summary>
  protected class OutboxRow {
    public Guid MessageId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EventData { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
  }
}
