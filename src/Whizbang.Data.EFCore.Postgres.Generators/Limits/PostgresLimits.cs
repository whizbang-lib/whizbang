using Whizbang.Generators.Shared.Limits;

namespace Whizbang.Data.EFCore.Postgres.Generators.Limits;

/// <summary>
/// <docs>infrastructure/database-limits</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Limits/PostgresLimitsTests.cs</tests>
/// PostgreSQL identifier limits.
/// PostgreSQL uses 63 bytes as the maximum identifier length (NAMEDATALEN - 1).
/// </summary>
/// <remarks>
/// PostgreSQL's NAMEDATALEN is 64 by default, with the last byte reserved for null terminator.
/// This results in a maximum identifier length of 63 bytes.
///
/// Note: This is measured in bytes, not characters. Multi-byte UTF-8 characters
/// will consume more than one byte toward the limit.
///
/// Reference: https://www.postgresql.org/docs/current/limits.html
/// </remarks>
public sealed class PostgresLimits : IDbProviderLimits {
  /// <summary>
  /// PostgreSQL maximum identifier length (NAMEDATALEN - 1 = 63 bytes).
  /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores - project convention uses SCREAMING_SNAKE_CASE for constants
  public const int MAX_IDENTIFIER_BYTES = 63;
#pragma warning restore CA1707

  /// <inheritdoc />
  public int MaxTableNameBytes => MAX_IDENTIFIER_BYTES;

  /// <inheritdoc />
  public int MaxColumnNameBytes => MAX_IDENTIFIER_BYTES;

  /// <inheritdoc />
  public int MaxIndexNameBytes => MAX_IDENTIFIER_BYTES;

  /// <inheritdoc />
  public string ProviderName => "PostgreSQL";

  /// <summary>
  /// Singleton instance for generator use.
  /// </summary>
  public static PostgresLimits Instance { get; } = new();

  private PostgresLimits() { }
}
