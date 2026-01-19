namespace Whizbang.Core.Messaging;

/// <summary>
/// Status flags for tracking receptor processing of events.
/// Stored in wh_receptor_processing table to track which receptors have processed which events.
/// Multiple flags can be combined using bitwise OR.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:ReceptorProcessing_ShouldHaveForeignKeyToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:ReceptorProcessing_ShouldHaveUniqueConstraintAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:PartialIndexes_ShouldExistForStatusQueriesAsync</tests>
[Flags]
public enum ReceptorProcessingStatus {
  /// <summary>
  /// No processing has occurred.
  /// </summary>
  None = 0,

  /// <summary>
  /// Receptor is currently processing this event.
  /// Indicates work in progress.
  /// </summary>
  Processing = 1 << 0,

  /// <summary>
  /// Receptor has successfully completed processing this event.
  /// Final successful state.
  /// </summary>
  Completed = 1 << 1,

  /// <summary>
  /// Receptor processing failed for this event.
  /// May be retried later based on retry policy.
  /// </summary>
  Failed = 1 << 2

  // Bits 3-15 reserved for future use
}
