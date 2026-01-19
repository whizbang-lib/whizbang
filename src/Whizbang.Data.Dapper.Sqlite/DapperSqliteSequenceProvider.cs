using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of ISequenceProvider using Dapper.
/// SQLite doesn't support RETURNING clause, so we use a different approach.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_DifferentStreamKeys_ShouldMaintainSeparateSequencesAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:CancellationToken_WhenCancelled_ShouldThrowAsync</tests>
public class DapperSqliteSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor) : DapperSequenceProviderBase(connectionFactory, executor) {
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
