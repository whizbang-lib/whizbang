using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IEventStore using Dapper.
/// Uses PostgreSQL advisory locks for thread-safe sequence number generation.
/// </summary>
public class DapperPostgresEventStore : DapperEventStoreBase {
  private static readonly JsonSerializerOptions _postgresJsonOptions = new() {
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = null,
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    IncludeFields = true
  };

  public DapperPostgresEventStore(IDbConnectionFactory connectionFactory, IDbExecutor executor)
    : base(connectionFactory, executor) {
  }

  /// <summary>
  /// Appends an event to a stream with thread-safe sequence number generation.
  /// Uses PostgreSQL's RETURNING clause for atomic insert-and-return.
  /// </summary>
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public override async Task AppendAsync(string streamKey, IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);
    ArgumentNullException.ThrowIfNull(envelope);

    const int maxRetries = 10;
    var lastException = default(Exception);

    for (int attempt = 0; attempt < maxRetries; attempt++) {
      try {
        using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
        // Connection is already opened by PostgresConnectionFactory

        // Get next sequence number
        var lastSequence = await Executor.ExecuteScalarAsync<long?>(
          connection,
          GetLastSequenceSql(),
          new { StreamKey = streamKey },
          cancellationToken: cancellationToken);

        var nextSequence = (lastSequence ?? -1) + 1;

        // Serialize envelope
        var json = JsonSerializer.Serialize(envelope, envelope.GetType(), _postgresJsonOptions);

        // Try to insert with sequence number
        await Executor.ExecuteAsync(
          connection,
          GetAppendSql(),
          new {
            StreamKey = streamKey,
            SequenceNumber = nextSequence,
            Envelope = json,
            CreatedAt = DateTimeOffset.UtcNow
          },
          cancellationToken: cancellationToken);

        // Success - exit retry loop
        return;
      } catch (Exception ex) when (IsUniqueConstraintViolation(ex)) {
        // UNIQUE constraint violation - another thread inserted the same sequence
        // Retry with next sequence number
        lastException = ex;
        await Task.Delay(10 * (attempt + 1), cancellationToken); // Exponential backoff
      }
    }

    // Max retries exceeded
    throw new InvalidOperationException(
      $"Failed to append event to stream '{streamKey}' after {maxRetries} attempts due to concurrent writes.",
      lastException);
  }

  internal static bool IsUniqueConstraintViolation(Exception ex) {
    // Check for PostgreSQL UNIQUE constraint violation
    // Error code 23505 = unique_violation
    if (ex is Npgsql.PostgresException pgEx) {
      return pgEx.SqlState == "23505";
    }
    return ex.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
  }

  protected override string GetAppendSql() => @"
    INSERT INTO whizbang_event_store (stream_key, sequence_number, envelope, created_at)
    VALUES (@StreamKey, @SequenceNumber, @Envelope, @CreatedAt)";

  protected override string GetReadSql() => @"
    SELECT envelope AS Envelope
    FROM whizbang_event_store
    WHERE stream_key = @StreamKey AND sequence_number >= @FromSequence
    ORDER BY sequence_number";

  protected override string GetLastSequenceSql() => @"
    SELECT COALESCE(MAX(sequence_number), -1)
    FROM whizbang_event_store
    WHERE stream_key = @StreamKey";
}
