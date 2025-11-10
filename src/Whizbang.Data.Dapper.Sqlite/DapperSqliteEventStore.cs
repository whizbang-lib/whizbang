using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IEventStore using Dapper.
/// Overrides AppendAsync to use transactions for thread-safe sequence number generation.
/// </summary>
public class DapperSqliteEventStore : DapperEventStoreBase {
  private static readonly JsonSerializerOptions _sqliteJsonOptions = new() {
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = null,
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    IncludeFields = true
  };

  public DapperSqliteEventStore(IDbConnectionFactory connectionFactory, IDbExecutor executor)
    : base(connectionFactory, executor) {
  }

  /// <summary>
  /// Appends an event to a stream with thread-safe sequence number generation.
  /// Uses retry logic with the UNIQUE constraint to handle concurrent writes.
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
        connection.Open();

        // Get next sequence number
        var lastSequence = await Executor.ExecuteScalarAsync<long?>(
          connection,
          GetLastSequenceSql(),
          new { StreamKey = streamKey },
          cancellationToken: cancellationToken);

        var nextSequence = (lastSequence ?? -1) + 1;

        // Serialize envelope
        var json = JsonSerializer.Serialize(envelope, envelope.GetType(), _sqliteJsonOptions);

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

  private static bool IsUniqueConstraintViolation(Exception ex) {
    // Check for SQLite UNIQUE constraint violation
    // Error code 19 = SQLITE_CONSTRAINT
    if (ex is Microsoft.Data.Sqlite.SqliteException sqliteEx) {
      return sqliteEx.SqliteErrorCode == 19;
    }
    return ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("Error 19", StringComparison.OrdinalIgnoreCase);
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
