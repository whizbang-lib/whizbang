namespace Whizbang.Data.Schema;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasUuidTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasStringTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasTimestampTzTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasJsonTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasBigIntTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasIntegerTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasSmallIntTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasBooleanTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_HasExactlyEightTypesAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_AllValuesAreUniqueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/WhizbangDataTypeTests.cs:WhizbangDataType_ToStringReturnsCorrectNamesAsync</tests>
/// Database-agnostic type system for Whizbang infrastructure tables.
/// These types are mapped to database-specific types by implementations (Postgres, SQLite, SQL Server).
/// </summary>
public enum WhizbangDataType {
  /// <summary>
  /// Universally Unique Identifier (UUID/GUID).
  /// Maps to: UUID (Postgres), BLOB (SQLite), UNIQUEIDENTIFIER (SQL Server)
  /// </summary>
  UUID,

  /// <summary>
  /// Variable-length string with optional max length.
  /// Maps to: VARCHAR(n)/TEXT (Postgres), TEXT (SQLite), NVARCHAR(n) (SQL Server)
  /// </summary>
  STRING,

  /// <summary>
  /// Timestamp with timezone.
  /// Maps to: TIMESTAMPTZ (Postgres), TEXT (SQLite ISO8601), DATETIMEOFFSET (SQL Server)
  /// </summary>
  TIMESTAMP_TZ,

  /// <summary>
  /// JSON/JSONB data type.
  /// Maps to: JSONB (Postgres), TEXT (SQLite), NVARCHAR(MAX) (SQL Server)
  /// </summary>
  JSON,

  /// <summary>
  /// 64-bit signed integer.
  /// Maps to: BIGINT (Postgres), INTEGER (SQLite), BIGINT (SQL Server)
  /// </summary>
  BIG_INT,

  /// <summary>
  /// 32-bit signed integer.
  /// Maps to: INTEGER (Postgres), INTEGER (SQLite), INT (SQL Server)
  /// </summary>
  INTEGER,

  /// <summary>
  /// 16-bit signed integer (commonly used for flags and small enums).
  /// Maps to: SMALLINT (Postgres), INTEGER (SQLite), SMALLINT (SQL Server)
  /// </summary>
  SMALL_INT,

  /// <summary>
  /// Boolean type.
  /// Maps to: BOOLEAN (Postgres), INTEGER 0/1 (SQLite), BIT (SQL Server)
  /// </summary>
  BOOLEAN
}
