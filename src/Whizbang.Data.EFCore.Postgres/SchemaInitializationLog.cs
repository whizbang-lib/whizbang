using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Source-generated logging methods for schema initialization advisory lock operations.
/// These are defined in the runtime library (not in generated code) because Roslyn source generators
/// cannot see other generators' output — [LoggerMessage] attributes in generated code won't be
/// processed by Microsoft's logger source generator.
/// </summary>
/// <docs>data/turnkey-initialization#multi-instance</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs</tests>
public static partial class SchemaInitializationLog {
  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Acquiring advisory lock {LockId} for schema '{Schema}'...")]
  public static partial void AcquiringAdvisoryLock(ILogger logger, int lockId, string schema);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Advisory lock {LockId} held by another instance, retrying (attempt {Attempt}, delay {DelayMs}ms)...")]
  public static partial void AdvisoryLockRetry(ILogger logger, int lockId, int attempt, int delayMs);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Acquired advisory lock {LockId} for schema '{Schema}'")]
  public static partial void AcquiredAdvisoryLock(ILogger logger, int lockId, string schema);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Transaction committed, advisory lock {LockId} released for schema '{Schema}'")]
  public static partial void TransactionCommitted(ILogger logger, int lockId, string schema);

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
}
