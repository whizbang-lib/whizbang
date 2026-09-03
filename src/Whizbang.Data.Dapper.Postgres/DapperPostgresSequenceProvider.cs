using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of ISequenceProvider using Dapper.
/// Uses PostgreSQL's RETURNING clause for atomic operations.
/// </summary>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_DifferentStreamIds_ShouldMaintainSeparateSequencesAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:CancellationToken_WhenCanceled_ShouldThrowAsync</tests>
public class DapperPostgresSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor) : DapperSequenceProviderBase(connectionFactory, executor) {
  /// <summary>
  /// Returns the PostgreSQL-specific SQL for updating a sequence value atomically using RETURNING.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_DifferentStreamIds_ShouldMaintainSeparateSequencesAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
  protected override string GetUpdateSequenceSql() => @"
    UPDATE wh_sequences
    SET current_value = current_value + 1, last_updated_at = @Now
    WHERE sequence_name = @SequenceKey
    RETURNING current_value";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for inserting or updating a sequence using ON CONFLICT UPSERT.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
  protected override string GetInsertOrUpdateSequenceSql() => @"
    INSERT INTO wh_sequences (sequence_name, current_value, last_updated_at)
    VALUES (@SequenceKey, 0, @Now)
    ON CONFLICT (sequence_name) DO UPDATE
    SET current_value = wh_sequences.current_value + 1,
        last_updated_at = @Now
    RETURNING current_value";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for retrieving the current sequence value without incrementing.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
  protected override string GetCurrentSequenceSql() => @"
    SELECT current_value
    FROM wh_sequences
    WHERE sequence_name = @SequenceKey";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for resetting a sequence to a specific value using UPSERT.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/SequenceProviderContractTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
  protected override string GetResetSequenceSql() => @"
    INSERT INTO wh_sequences (sequence_name, current_value, last_updated_at)
    VALUES (@SequenceKey, @NewValue - 1, @Now)
    ON CONFLICT (sequence_name) DO UPDATE
    SET current_value = @NewValue - 1,
        last_updated_at = @Now";
}
