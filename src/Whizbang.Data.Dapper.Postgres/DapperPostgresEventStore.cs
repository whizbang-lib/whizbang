using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IEventStore using Dapper with 3-column JSONB pattern.
/// Stream ID is inferred from event's [AggregateId] property.
/// Uses JsonbSizeValidator for C#-based size validation.
/// </summary>
public class DapperPostgresEventStore : DapperEventStoreBase {
  private readonly IJsonbPersistenceAdapter<IMessageEnvelope> _adapter;
  private readonly JsonbSizeValidator _sizeValidator;
  private readonly IPolicyEngine _policyEngine;
  private readonly ILogger<DapperPostgresEventStore> _logger;

  public DapperPostgresEventStore(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerContext jsonContext,
    IJsonbPersistenceAdapter<IMessageEnvelope> adapter,
    JsonbSizeValidator sizeValidator,
    IPolicyEngine policyEngine,
    ILogger<DapperPostgresEventStore> logger
  ) : base(connectionFactory, executor, jsonContext) {
    _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    _sizeValidator = sizeValidator ?? throw new ArgumentNullException(nameof(sizeValidator));
    _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Appends an event to the specified stream (AOT-compatible).
  /// Stream ID is provided explicitly, avoiding reflection.
  /// Splits envelope into 3 JSONB columns, validates size, handles concurrent writes with retry.
  /// </summary>
  public override async Task AppendAsync(Guid streamId, IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    // Get policy configuration
    var payload = envelope.GetPayload();
    var policyCtx = new PolicyContext(payload, envelope);
    var policy = await _policyEngine.MatchAsync(policyCtx);

    // Split into 3 JSONB columns
    var jsonb = _adapter.ToJsonb(envelope, policy);

    // Validate size (calculates in C#, logs warnings, adds to metadata if threshold crossed)
    jsonb = _sizeValidator.Validate(jsonb, payload.GetType().Name, policy);

    const int maxRetries = 10;
    var lastException = default(Exception);

    for (int attempt = 0; attempt < maxRetries; attempt++) {
      try {
        using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
        EnsureConnectionOpen(connection);

        // Get next sequence number
        var lastSequence = await GetLastSequenceAsync(streamId, cancellationToken);
        var nextSequence = lastSequence + 1;

        // INSERT with 3 JSONB columns
        await Executor.ExecuteAsync(
          connection,
          GetAppendSql(),
          new {
            EventId = envelope.MessageId.Value,
            StreamId = streamId,
            SequenceNumber = nextSequence,
            EventType = payload.GetType().FullName,
            EventData = jsonb.DataJson,
            Metadata = jsonb.MetadataJson,
            Scope = jsonb.ScopeJson,
            CreatedAt = DateTimeOffset.UtcNow
          },
          cancellationToken: cancellationToken);

        // Success - exit retry loop
        return;
      } catch (Exception ex) when (IsUniqueConstraintViolation(ex)) {
        // UNIQUE constraint violation - concurrent write, retry
        lastException = ex;
        await Task.Delay(10 * (attempt + 1), cancellationToken);
      }
    }

    // Max retries exceeded
    throw new InvalidOperationException(
      $"Failed to append event to stream '{streamId}' after {maxRetries} attempts due to concurrent writes.",
      lastException);
  }

  /// <summary>
  /// Reads events from a stream by stream ID (UUID).
  /// Reconstructs envelope from 3 JSONB columns.
  /// </summary>
  public override async IAsyncEnumerable<IMessageEnvelope> ReadAsync(
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
      var jsonb = new JsonbPersistenceModel {
        DataJson = row.EventData,
        MetadataJson = row.Metadata,
        ScopeJson = row.Scope
      };

      var envelope = _adapter.FromJsonb(jsonb);
      yield return envelope;
    }
  }

  internal static bool IsUniqueConstraintViolation(Exception ex) {
    if (ex is Npgsql.PostgresException pgEx) {
      return pgEx.SqlState == "23505"; // unique_violation
    }
    return ex.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
  }

  protected override string GetAppendSql() => @"
    INSERT INTO whizbang_event_store
      (event_id, stream_id, sequence_number, event_type, event_data, metadata, scope, created_at)
    VALUES
      (@EventId, @StreamId, @SequenceNumber, @EventType,
       @EventData::jsonb, @Metadata::jsonb, @Scope::jsonb, @CreatedAt)";

  protected override string GetReadSql() => @"
    SELECT
      event_data::text AS EventData,
      metadata::text AS Metadata,
      scope::text AS Scope
    FROM whizbang_event_store
    WHERE stream_id = @StreamId AND sequence_number >= @FromSequence
    ORDER BY sequence_number";

  protected override string GetLastSequenceSql() => @"
    SELECT COALESCE(MAX(sequence_number), -1)
    FROM whizbang_event_store
    WHERE stream_id = @StreamId";
}
