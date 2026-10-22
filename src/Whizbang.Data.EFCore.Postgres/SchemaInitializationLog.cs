// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Source-generated logging methods for schema initialization advisory lock operations.
/// These are defined in the runtime library (not in generated code) because Roslyn source generators
/// cannot see other generators' output — [LoggerMessage] attributes in generated code won't be
/// processed by Microsoft's logger source generator.
/// </summary>
/// <docs>data/turnkey-initialization#multi-instance</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs:ConcurrentInitialization_BothPodsSucceedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs:Initialization_NeverVacuumsTheWorkTablesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationTests.cs:EnsureWhizbangDatabaseInitialized_CreatesCoreInfrastructureTablesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaStepRunnerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupMigrationStepsTests.cs</tests>
public static partial class SchemaInitializationLog {
  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Acquiring advisory lock {LockId} for schema '{Schema}'...")]
  public static partial void AcquiringAdvisoryLock(ILogger logger, long lockId, string schema);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Advisory lock {LockId} held by another instance, retrying (attempt {Attempt}, delay {DelayMs}ms)...")]
  public static partial void AdvisoryLockRetry(ILogger logger, long lockId, int attempt, int delayMs);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Acquired advisory lock {LockId} for schema '{Schema}'")]
  public static partial void AcquiredAdvisoryLock(ILogger logger, long lockId, string schema);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Transaction committed, advisory lock {LockId} released for schema '{Schema}'")]
  public static partial void TransactionCommitted(ILogger logger, long lockId, string schema);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Failed to close database connection for schema '{Schema}'")]
  public static partial void FailedToCloseConnection(ILogger logger, Exception ex, string schema);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Schema initialization transient failure for schema '{Schema}', retrying (attempt {Attempt}, delay {DelayMs}ms): {Error}")]
  public static partial void InitializationRetry(ILogger logger, string schema, int attempt, int delayMs, string error);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Using '{ConnectionStringName}-init' connection string for schema initialization")]
  public static partial void UsingInitConnectionString(ILogger logger, string connectionStringName);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Schema up to date for '{Schema}' (all hashes match), skipping initialization")]
  public static partial void SchemaUpToDate(ILogger logger, string schema);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Schema changes detected for '{Schema}': infrastructure={InfraChanged}, perspectives={PerspChanged}")]
  public static partial void SchemaChangesDetected(ILogger logger, string schema, bool infraChanged, bool perspChanged);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Schema initialized by another instance for '{Schema}', skipping")]
  public static partial void SchemaInitializedByOtherInstance(ILogger logger, string schema);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Migration {Position} of {Total} - Started : {Step}")]
  internal static partial void StepStarted(ILogger logger, int position, int total, string step);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Migration {Position} of {Total} - Succeeded : {Step} ({Duration})")]
  internal static partial void StepSucceeded(ILogger logger, int position, int total, string step, StepDuration duration);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Migration {Position} of {Total} - Skipped : {Step} (already current)")]
  internal static partial void StepSkipped(ILogger logger, int position, int total, string step);

  [LoggerMessage(
      Level = LogLevel.Error,
      Message = "Migration {Position} of {Total} - Failed : {Step} : {SqlState} {Error}")]
  internal static partial void StepFailed(ILogger logger, Exception ex, int position, int total, string step, string sqlState, string error);

  [LoggerMessage(
      Level = LogLevel.Error,
      Message = "Migration {Position} of {Total} - Failed : {Step} : {SqlState} {Error}; retrying in {Delay} at {RetryAt}")]
  internal static partial void StepFailedWillRetry(
      ILogger logger, Exception ex, int position, int total, string step, string sqlState, string error, StepDuration delay, string retryAt);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Migration complete: {Succeeded} of {Total} succeeded ({Applied} applied, {Skipped} skipped) in {Duration}")]
  internal static partial void PassComplete(ILogger logger, int succeeded, int total, int applied, int skipped, StepDuration duration);

  [LoggerMessage(
      Level = LogLevel.Error,
      Message = "Migration stopped at {Position} of {Total}: {Step} : {Error}")]
  internal static partial void PassStopped(ILogger logger, int position, int total, string step, string error);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Could not roll back schema step {Step} for '{Schema}'; the server ends a transaction whose connection is gone, and the step's own failure is what is raised")]
  internal static partial void StepRollbackFailed(ILogger logger, Exception ex, string schema, string step);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Could not record the failure of schema step {Step} for '{Schema}' in wh_schema_migrations; the step's own failure is what is raised")]
  internal static partial void StepFailureNotRecorded(ILogger logger, Exception ex, string schema, string step);
}

/// <summary>A step's or a pass's duration as the step lines give it: seconds to the millisecond, as in <c>1.234s</c>.</summary>
/// <param name="Elapsed">The duration.</param>
internal readonly record struct StepDuration(TimeSpan Elapsed) {
  /// <inheritdoc/>
  public override string ToString() =>
    Elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "s";
}
