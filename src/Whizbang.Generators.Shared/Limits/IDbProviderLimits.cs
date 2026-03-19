namespace Whizbang.Generators.Shared.Limits;

/// <summary>
/// <docs>operations/infrastructure/database-limits</docs>
/// <tests>tests/Whizbang.Generators.Tests/Limits/DbProviderLimitsTests.cs</tests>
/// Defines identifier length limits for a database provider.
/// Used by source generators to validate names at compile time.
/// </summary>
/// <remarks>
/// Each database provider has different maximum lengths for identifiers:
/// - PostgreSQL: 63 bytes (NAMEDATALEN - 1)
/// - MySQL: 64 characters
/// - SQL Server: 128 characters
///
/// Provider-specific implementations live in their respective generator packages.
/// </remarks>
public interface IDbProviderLimits {
  /// <summary>
  /// Maximum length in bytes for table identifiers.
  /// </summary>
  int MaxTableNameBytes { get; }

  /// <summary>
  /// Maximum length in bytes for column identifiers.
  /// </summary>
  int MaxColumnNameBytes { get; }

  /// <summary>
  /// Maximum length in bytes for index identifiers.
  /// </summary>
  int MaxIndexNameBytes { get; }

  /// <summary>
  /// Database provider name for error messages.
  /// </summary>
  string ProviderName { get; }
}
