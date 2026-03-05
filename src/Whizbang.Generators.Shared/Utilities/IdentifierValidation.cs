using System.Text;
using Whizbang.Generators.Shared.Limits;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// <docs>infrastructure/database-limits</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/IdentifierValidationTests.cs</tests>
/// Utilities for validating database identifiers against provider limits.
/// </summary>
/// <remarks>
/// All validation is performed in bytes (UTF-8) not characters, because:
/// - PostgreSQL uses byte-based limits
/// - Multi-byte characters (e.g., Unicode) consume multiple bytes
/// </remarks>
public static class IdentifierValidation {
  /// <summary>
  /// Validates a table name against provider limits.
  /// </summary>
  /// <param name="tableName">The table name to validate</param>
  /// <param name="limits">The database provider limits</param>
  /// <returns>Error message if invalid, null if valid</returns>
  public static string? ValidateTableName(string tableName, IDbProviderLimits limits) {
    if (string.IsNullOrEmpty(tableName)) {
      return null;
    }

    var byteCount = GetByteCount(tableName);
    if (byteCount > limits.MaxTableNameBytes) {
      return $"Table name '{tableName}' is {byteCount} bytes, exceeding {limits.ProviderName} limit of {limits.MaxTableNameBytes} bytes";
    }
    return null;
  }

  /// <summary>
  /// Validates a column name against provider limits.
  /// </summary>
  /// <param name="columnName">The column name to validate</param>
  /// <param name="limits">The database provider limits</param>
  /// <returns>Error message if invalid, null if valid</returns>
  public static string? ValidateColumnName(string columnName, IDbProviderLimits limits) {
    if (string.IsNullOrEmpty(columnName)) {
      return null;
    }

    var byteCount = GetByteCount(columnName);
    if (byteCount > limits.MaxColumnNameBytes) {
      return $"Column name '{columnName}' is {byteCount} bytes, exceeding {limits.ProviderName} limit of {limits.MaxColumnNameBytes} bytes";
    }
    return null;
  }

  /// <summary>
  /// Validates an index name against provider limits.
  /// </summary>
  /// <param name="indexName">The index name to validate</param>
  /// <param name="limits">The database provider limits</param>
  /// <returns>Error message if invalid, null if valid</returns>
  public static string? ValidateIndexName(string indexName, IDbProviderLimits limits) {
    if (string.IsNullOrEmpty(indexName)) {
      return null;
    }

    var byteCount = GetByteCount(indexName);
    if (byteCount > limits.MaxIndexNameBytes) {
      return $"Index name '{indexName}' is {byteCount} bytes, exceeding {limits.ProviderName} limit of {limits.MaxIndexNameBytes} bytes";
    }
    return null;
  }

  /// <summary>
  /// Gets the byte count for an identifier using UTF-8 encoding.
  /// </summary>
  /// <param name="identifier">The identifier to measure</param>
  /// <returns>The byte count</returns>
  public static int GetByteCount(string identifier) {
    if (string.IsNullOrEmpty(identifier)) {
      return 0;
    }
    return Encoding.UTF8.GetByteCount(identifier);
  }

  /// <summary>
  /// Checks if a table name is within provider limits.
  /// </summary>
  /// <param name="tableName">The table name to check</param>
  /// <param name="limits">The database provider limits</param>
  /// <returns>True if within limits, false otherwise</returns>
  public static bool IsTableNameValid(string tableName, IDbProviderLimits limits) {
    return ValidateTableName(tableName, limits) is null;
  }

  /// <summary>
  /// Checks if a column name is within provider limits.
  /// </summary>
  /// <param name="columnName">The column name to check</param>
  /// <param name="limits">The database provider limits</param>
  /// <returns>True if within limits, false otherwise</returns>
  public static bool IsColumnNameValid(string columnName, IDbProviderLimits limits) {
    return ValidateColumnName(columnName, limits) is null;
  }

  /// <summary>
  /// Checks if an index name is within provider limits.
  /// </summary>
  /// <param name="indexName">The index name to check</param>
  /// <param name="limits">The database provider limits</param>
  /// <returns>True if within limits, false otherwise</returns>
  public static bool IsIndexNameValid(string indexName, IDbProviderLimits limits) {
    return ValidateIndexName(indexName, limits) is null;
  }
}
