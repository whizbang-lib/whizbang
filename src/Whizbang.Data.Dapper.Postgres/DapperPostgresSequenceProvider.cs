using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of ISequenceProvider using Dapper.
/// Uses PostgreSQL's RETURNING clause for atomic operations.
/// </summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_DifferentStreamKeys_ShouldMaintainSeparateSequencesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:CancellationToken_WhenCancelled_ShouldThrowAsync</tests>
public class DapperPostgresSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor) : DapperSequenceProviderBase(connectionFactory, executor) {
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_DifferentStreamKeys_ShouldMaintainSeparateSequencesAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
  protected override string GetUpdateSequenceSql() => @"
    UPDATE whizbang_sequences
    SET current_value = current_value + 1, last_updated_at = @Now
    WHERE sequence_name = @SequenceKey
    RETURNING current_value";

  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
  protected override string GetInsertOrUpdateSequenceSql() => @"
    INSERT INTO whizbang_sequences (sequence_name, current_value, last_updated_at)
    VALUES (@SequenceKey, 0, @Now)
    ON CONFLICT (sequence_name) DO UPDATE
    SET current_value = whizbang_sequences.current_value + 1,
        last_updated_at = @Now
    RETURNING current_value";

  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
  protected override string GetCurrentSequenceSql() => @"
    SELECT current_value
    FROM whizbang_sequences
    WHERE sequence_name = @SequenceKey";

  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
  protected override string GetResetSequenceSql() => @"
    INSERT INTO whizbang_sequences (sequence_name, current_value, last_updated_at)
    VALUES (@SequenceKey, @NewValue - 1, @Now)
    ON CONFLICT (sequence_name) DO UPDATE
    SET current_value = @NewValue - 1,
        last_updated_at = @Now";
}
