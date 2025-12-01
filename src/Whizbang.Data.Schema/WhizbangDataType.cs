namespace Whizbang.Data.Schema;

/// <summary>
/// Database-agnostic type system for Whizbang infrastructure tables.
/// These types are mapped to database-specific types by implementations (Postgres, SQLite, SQL Server).
/// </summary>
public enum WhizbangDataType
{
    /// <summary>
    /// Universally Unique Identifier (UUID/GUID).
    /// Maps to: UUID (Postgres), BLOB (SQLite), UNIQUEIDENTIFIER (SQL Server)
    /// </summary>
    Uuid,

    /// <summary>
    /// Variable-length string with optional max length.
    /// Maps to: VARCHAR(n)/TEXT (Postgres), TEXT (SQLite), NVARCHAR(n) (SQL Server)
    /// </summary>
    String,

    /// <summary>
    /// Timestamp with timezone.
    /// Maps to: TIMESTAMPTZ (Postgres), TEXT (SQLite ISO8601), DATETIMEOFFSET (SQL Server)
    /// </summary>
    TimestampTz,

    /// <summary>
    /// JSON/JSONB data type.
    /// Maps to: JSONB (Postgres), TEXT (SQLite), NVARCHAR(MAX) (SQL Server)
    /// </summary>
    Json,

    /// <summary>
    /// 64-bit signed integer.
    /// Maps to: BIGINT (Postgres), INTEGER (SQLite), BIGINT (SQL Server)
    /// </summary>
    BigInt,

    /// <summary>
    /// 32-bit signed integer.
    /// Maps to: INTEGER (Postgres), INTEGER (SQLite), INT (SQL Server)
    /// </summary>
    Integer,

    /// <summary>
    /// Boolean type.
    /// Maps to: BOOLEAN (Postgres), INTEGER 0/1 (SQLite), BIT (SQL Server)
    /// </summary>
    Boolean
}
