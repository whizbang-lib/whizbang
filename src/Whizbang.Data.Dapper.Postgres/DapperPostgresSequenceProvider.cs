using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of ISequenceProvider using Dapper.
/// Uses PostgreSQL's RETURNING clause for atomic operations.
/// </summary>
public class DapperPostgresSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor) : DapperSequenceProviderBase(connectionFactory, executor) {
  protected override string GetUpdateSequenceSql() => @"
    UPDATE whizbang_sequences
    SET current_value = current_value + 1, last_updated_at = @Now
    WHERE sequence_key = @SequenceKey
    RETURNING current_value";

  protected override string GetInsertOrUpdateSequenceSql() => @"
    INSERT INTO whizbang_sequences (sequence_key, current_value, last_updated_at)
    VALUES (@SequenceKey, 0, @Now)
    ON CONFLICT (sequence_key) DO UPDATE
    SET current_value = whizbang_sequences.current_value + 1,
        last_updated_at = @Now
    RETURNING current_value";

  protected override string GetCurrentSequenceSql() => @"
    SELECT current_value
    FROM whizbang_sequences
    WHERE sequence_key = @SequenceKey";

  protected override string GetResetSequenceSql() => @"
    INSERT INTO whizbang_sequences (sequence_key, current_value, last_updated_at)
    VALUES (@SequenceKey, @NewValue - 1, @Now)
    ON CONFLICT (sequence_key) DO UPDATE
    SET current_value = @NewValue - 1,
        last_updated_at = @Now";
}
