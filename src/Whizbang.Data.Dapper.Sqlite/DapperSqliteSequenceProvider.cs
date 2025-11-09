using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of ISequenceProvider using Dapper.
/// SQLite doesn't support RETURNING clause, so we use a different approach.
/// </summary>
public class DapperSqliteSequenceProvider : DapperSequenceProviderBase {
  public DapperSqliteSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor)
    : base(connectionFactory, executor) {
  }

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
