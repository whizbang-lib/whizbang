using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Sequencing;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of ISequenceProvider for durable, monotonic sequence generation.
/// Uses database transactions to ensure thread-safety and no gaps in sequence numbers.
/// </summary>
public class DapperSequenceProvider : ISequenceProvider {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;

  public DapperSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
  }

  public async Task<long> GetNextAsync(string streamKey, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    using var connection = await _connectionFactory.CreateConnectionAsync(ct);
    connection.Open();

    using var transaction = connection.BeginTransaction();

    try {
      // Try to increment existing sequence
      const string updateSql = @"
        UPDATE whizbang_sequences
        SET current_value = current_value + 1, last_updated_at = @Now
        WHERE sequence_key = @SequenceKey
        RETURNING current_value";

      var updatedValue = await _executor.ExecuteScalarAsync<long?>(
        connection,
        updateSql,
        new {
          SequenceKey = streamKey,
          Now = DateTimeOffset.UtcNow
        },
        transaction,
        ct);

      if (updatedValue.HasValue) {
        transaction.Commit();
        return updatedValue.Value;
      }

      // Sequence doesn't exist, insert new one starting at 0
      const string insertSql = @"
        INSERT INTO whizbang_sequences (sequence_key, current_value, last_updated_at)
        VALUES (@SequenceKey, 0, @Now)
        ON CONFLICT (sequence_key) DO UPDATE
        SET current_value = whizbang_sequences.current_value + 1,
            last_updated_at = @Now
        RETURNING current_value";

      var newValue = await _executor.ExecuteScalarAsync<long>(
        connection,
        insertSql,
        new {
          SequenceKey = streamKey,
          Now = DateTimeOffset.UtcNow
        },
        transaction,
        ct);

      transaction.Commit();
      return newValue;
    } catch {
      transaction.Rollback();
      throw;
    }
  }

  public async Task<long> GetCurrentAsync(string streamKey, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    using var connection = await _connectionFactory.CreateConnectionAsync(ct);
    connection.Open();

    const string sql = @"
      SELECT current_value
      FROM whizbang_sequences
      WHERE sequence_key = @SequenceKey";

    var currentValue = await _executor.ExecuteScalarAsync<long?>(
      connection,
      sql,
      new { SequenceKey = streamKey },
      cancellationToken: ct);

    return currentValue ?? -1;
  }

  public async Task ResetAsync(string streamKey, long newValue = 0, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    using var connection = await _connectionFactory.CreateConnectionAsync(ct);
    connection.Open();

    const string sql = @"
      INSERT INTO whizbang_sequences (sequence_key, current_value, last_updated_at)
      VALUES (@SequenceKey, @NewValue - 1, @Now)
      ON CONFLICT (sequence_key) DO UPDATE
      SET current_value = @NewValue - 1,
          last_updated_at = @Now";

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        SequenceKey = streamKey,
        NewValue = newValue,
        Now = DateTimeOffset.UtcNow
      },
      cancellationToken: ct);
  }
}
