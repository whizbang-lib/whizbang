using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IInbox implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// Uses JSONB adapter for envelope serialization - mirrors DapperOutboxBase.
/// Supports both immediate processing (with instanceId) and polling-based (without instanceId).
/// </summary>
public abstract class DapperInboxBase : IInbox {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;
  protected readonly IJsonbPersistenceAdapter<IMessageEnvelope> _envelopeAdapter;
  protected readonly Guid? _instanceId;
  protected readonly int _leaseSeconds;

  /// <summary>
  /// Creates a new DapperInboxBase instance.
  /// </summary>
  /// <param name="connectionFactory">Database connection factory</param>
  /// <param name="executor">Database command executor</param>
  /// <param name="envelopeAdapter">JSONB persistence adapter for envelopes</param>
  /// <param name="instanceId">Service instance ID for lease-based coordination (optional, enables immediate processing)</param>
  /// <param name="leaseSeconds">Lease duration in seconds (default 300 = 5 minutes)</param>
  protected DapperInboxBase(
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
  /// Gets the SQL command to store a new inbox message using JSONB pattern.
  /// Parameters: @MessageId (Guid), @HandlerName (string), @EventType (string),
  ///             @EventData (string), @Metadata (string), @Scope (string, nullable),
  ///             @Status (string), @Attempts (int), @ReceivedAt (DateTimeOffset),
  ///             @InstanceId (Guid, nullable), @LeaseExpiry (DateTimeOffset, nullable)
  /// </summary>
  protected abstract string GetStoreSql();

  /// <summary>
  /// Gets the SQL query to retrieve pending inbox messages using JSONB pattern.
  /// Should return: MessageId, HandlerName, EventType, EventData, Metadata, Scope, ReceivedAt
  /// Parameters: @BatchSize (int)
  /// </summary>
  protected abstract string GetPendingSql();

  /// <summary>
  /// Gets the SQL command to mark a message as processed.
  /// Parameters: @MessageId (Guid), @ProcessedAt (DateTimeOffset)
  /// </summary>
  protected abstract string GetMarkProcessedSql();

  /// <summary>
  /// Gets the SQL query to check if a message has been processed.
  /// Should return count of matching records where processed_at is not null.
  /// Parameters: @MessageId (Guid)
  /// </summary>
  protected abstract string GetHasProcessedSql();

  /// <summary>
  /// Gets the SQL command to delete expired inbox records.
  /// Parameters: @CutoffDate (DateTimeOffset)
  /// </summary>
  protected abstract string GetCleanupExpiredSql();

  public async Task StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string handlerName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(handlerName);

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
        HandlerName = handlerName,
        EventType = eventType,
        EventData = jsonbModel.DataJson,
        Metadata = jsonbModel.MetadataJson,
        Scope = jsonbModel.ScopeJson,
        Status = _instanceId.HasValue ? "Processing" : "Pending",
        Attempts = 0,
        ReceivedAt = now,
        InstanceId = _instanceId,
        LeaseExpiry = _instanceId.HasValue ? now.AddSeconds(_leaseSeconds) : (DateTimeOffset?)null
      },
      cancellationToken: cancellationToken);
  }

  public async Task<IReadOnlyList<InboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetPendingSql();

    var rows = await _executor.QueryAsync<InboxRow>(
      connection,
      sql,
      new { BatchSize = batchSize },
      cancellationToken: cancellationToken);

    return [.. rows.Select(r => new InboxMessage(
      MessageId.From(r.MessageId),
      r.HandlerName,
      r.EventType,
      r.EventData,
      r.Metadata,
      r.Scope,
      r.ReceivedAt
    ))];
  }

  public async Task MarkProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetMarkProcessedSql();

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = messageId.Value,
        ProcessedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  public async Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetHasProcessedSql();

    var count = await _executor.ExecuteScalarAsync<long>(
      connection,
      sql,
      new { MessageId = messageId.Value },
      cancellationToken: cancellationToken);

    return count > 0;
  }

  public async Task CleanupExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetCleanupExpiredSql();
    var cutoffDate = DateTimeOffset.UtcNow - retention;

    await _executor.ExecuteAsync(
      connection,
      sql,
      new { CutoffDate = cutoffDate },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// Maps to JSONB-based inbox schema.
  /// </summary>
  protected class InboxRow {
    public Guid MessageId { get; set; }
    public string HandlerName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EventData { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
  }
}
