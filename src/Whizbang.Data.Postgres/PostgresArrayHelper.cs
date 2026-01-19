using System;
using Npgsql;
using NpgsqlTypes;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Helper methods for working with PostgreSQL arrays.
/// Provides type-safe conversion between C# arrays and PostgreSQL array types.
/// </summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs</tests>
public static class PostgresArrayHelper {
  /// <summary>
  /// Converts a C# UUID array to a PostgreSQL UUID[] parameter.
  /// Returns empty array if input is null.
  /// </summary>
  /// <param name="guids">Array of GUIDs to convert</param>
  /// <returns>NpgsqlParameter configured for UUID[] type</returns>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:ToUuidArray_ValidGuidArray_CreatesUuidArrayParameterAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:ToUuidArray_NullInput_CreatesEmptyArrayParameterAsync</tests>
  public static NpgsqlParameter ToUuidArray(Guid[]? guids) {
    return new NpgsqlParameter {
      Value = guids ?? Array.Empty<Guid>(),
      NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
    };
  }

  /// <summary>
  /// Converts a C# string array to a PostgreSQL VARCHAR[] parameter.
  /// Returns empty array if input is null.
  /// </summary>
  /// <param name="strings">Array of strings to convert</param>
  /// <returns>NpgsqlParameter configured for VARCHAR[] type</returns>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:ToVarcharArray_ValidStringArray_CreatesVarcharArrayParameterAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:ToVarcharArray_NullInput_CreatesEmptyArrayParameterAsync</tests>
  public static NpgsqlParameter ToVarcharArray(string[]? strings) {
    return new NpgsqlParameter {
      Value = strings ?? Array.Empty<string>(),
      NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Varchar
    };
  }

  /// <summary>
  /// Converts a C# int array to a PostgreSQL INTEGER[] parameter.
  /// Returns empty array if input is null.
  /// </summary>
  /// <param name="integers">Array of integers to convert</param>
  /// <returns>NpgsqlParameter configured for INTEGER[] type</returns>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:ToIntegerArray_ValidIntArray_CreatesIntegerArrayParameterAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:ToIntegerArray_NullInput_CreatesEmptyArrayParameterAsync</tests>
  public static NpgsqlParameter ToIntegerArray(int[]? integers) {
    return new NpgsqlParameter {
      Value = integers ?? Array.Empty<int>(),
      NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer
    };
  }

  /// <summary>
  /// Creates an empty UUID[] parameter for PostgreSQL.
  /// Useful when no UUIDs need to be passed but the parameter is required.
  /// </summary>
  /// <returns>NpgsqlParameter configured for empty UUID[] array</returns>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresArrayHelperTests.cs:EmptyUuidArray_CreatesEmptyUuidArrayParameterAsync</tests>
  public static NpgsqlParameter EmptyUuidArray() {
    return ToUuidArray(Array.Empty<Guid>());
  }
}
