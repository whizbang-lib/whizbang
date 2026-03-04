using Npgsql;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// Note: TrackedGuid Dapper handler is automatically registered by Whizbang.Data.Dapper.Custom.
// This file only contains Npgsql-specific extensions for raw SQL operations.

/// <summary>
/// Extension methods for NpgsqlParameterCollection to handle TrackedGuid parameters.
/// </summary>
internal static class NpgsqlTrackedGuidExtensions {
  /// <summary>
  /// Adds a parameter with the specified name and TrackedGuid value.
  /// Converts TrackedGuid to Guid for PostgreSQL UUID column compatibility.
  /// </summary>
  /// <remarks>
  /// Use this method instead of AddWithValue when passing TrackedGuid values
  /// to Npgsql commands. Npgsql doesn't know how to serialize TrackedGuid natively.
  /// </remarks>
  public static NpgsqlParameter AddGuidValue(
    this NpgsqlParameterCollection parameters,
    string parameterName,
    TrackedGuid value) {
    // Convert TrackedGuid to Guid for Npgsql
    return parameters.AddWithValue(parameterName, (Guid)value);
  }

  /// <summary>
  /// Adds a parameter with the specified name and TrackedGuid array value.
  /// Converts TrackedGuid[] to Guid[] for PostgreSQL UUID[] column compatibility.
  /// </summary>
  public static NpgsqlParameter AddGuidArray(
    this NpgsqlParameterCollection parameters,
    string parameterName,
    TrackedGuid[] values) {
    // Convert TrackedGuid[] to Guid[] for Npgsql
    return parameters.AddWithValue(parameterName, values.Select(v => (Guid)v).ToArray());
  }
}
