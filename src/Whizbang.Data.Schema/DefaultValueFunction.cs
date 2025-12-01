namespace Whizbang.Data.Schema;

/// <summary>
/// Enum for database function-based default values.
/// Pure enum-based approach ensures type safety and no string matching in implementations.
/// </summary>
public enum DefaultValueFunction {
  /// <summary>
  /// Current timestamp/datetime in local timezone.
  /// Maps to: NOW() (Postgres), CURRENT_TIMESTAMP (SQLite/SQL Server)
  /// </summary>
  DateTime_Now,

  /// <summary>
  /// Current timestamp/datetime in UTC timezone.
  /// Maps to: (NOW() AT TIME ZONE 'UTC') (Postgres), datetime('now', 'utc') (SQLite), GETUTCDATE() (SQL Server)
  /// </summary>
  DateTime_UtcNow,

  /// <summary>
  /// Generate a new UUID/GUID.
  /// Maps to: gen_random_uuid() (Postgres), randomblob(16) or uuid() (SQLite with extension), NEWID() (SQL Server)
  /// </summary>
  Uuid_Generate,

  /// <summary>
  /// Boolean TRUE value.
  /// Maps to: TRUE (Postgres), 1 (SQLite), 1 (SQL Server)
  /// </summary>
  Boolean_True,

  /// <summary>
  /// Boolean FALSE value.
  /// Maps to: FALSE (Postgres), 0 (SQLite), 0 (SQL Server)
  /// </summary>
  Boolean_False
}
