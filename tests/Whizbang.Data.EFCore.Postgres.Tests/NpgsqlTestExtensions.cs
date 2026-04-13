using System.Dynamic;
using System.Globalization;
using Npgsql;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Lightweight Npgsql extension methods for test verification queries.
/// Replaces Dapper dependency with direct Npgsql operations so the EFCore
/// test project has no reference to Dapper assemblies or packages.
/// </summary>
internal static class NpgsqlTestExtensions {

  /// <summary>
  /// Executes a SQL command with no return value.
  /// Replaces Dapper's <c>conn.ExecuteAsync(sql)</c>.
  /// </summary>
  public static async Task ExecuteAsync(this NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Executes a parameterized SQL command with no return value.
  /// Replaces Dapper's <c>conn.ExecuteAsync(sql, param)</c>.
  /// </summary>
  public static async Task ExecuteAsync(this NpgsqlConnection conn, string sql, object param) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    AddParameters(cmd, param);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Executes a parameterized query and returns the first column of the first row.
  /// Replaces Dapper's <c>conn.ExecuteScalarAsync&lt;T&gt;(sql, param)</c>.
  /// </summary>
  public static async Task<T> ExecuteScalarAsync<T>(this NpgsqlConnection conn, string sql, object? param = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (param != null) {
      AddParameters(cmd, param);
    }
    var result = await cmd.ExecuteScalarAsync();
    if (result is T typed) {
      return typed;
    }
    if (result is null or DBNull) {
      return default!;
    }
    return (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Executes a query and returns the first row as a dynamic ExpandoObject.
  /// Replaces Dapper's non-generic <c>conn.QueryFirstAsync(sql)</c>.
  /// </summary>
  public static async Task<dynamic> QueryFirstAsync(this NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException("Sequence contains no elements");
    }
    return ToDynamic(reader);
  }

  /// <summary>
  /// Executes a query and returns the first column of the first row, cast to <typeparamref name="T"/>.
  /// Replaces Dapper's <c>conn.QueryFirstAsync&lt;int&gt;(sql)</c> for scalar types.
  /// </summary>
  public static async Task<T> QueryFirstAsync<T>(this NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync();
    if (result is T typed) {
      return typed;
    }
    return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Executes a parameterized query and returns the single result row as a dynamic ExpandoObject.
  /// Replaces Dapper's non-generic <c>conn.QuerySingleAsync(sql, param)</c>.
  /// </summary>
  public static async Task<dynamic> QuerySingleAsync(this NpgsqlConnection conn, string sql, object? param = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (param != null) {
      AddParameters(cmd, param);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException("Sequence contains no elements");
    }
    return ToDynamic(reader);
  }

  /// <summary>
  /// Executes a query and maps the single result row to <typeparamref name="T"/>.
  /// Supports scalar types, ValueTuples, and <c>dynamic</c> (returns ExpandoObject).
  /// Replaces Dapper's <c>conn.QuerySingleAsync&lt;T&gt;(sql, param)</c>.
  /// </summary>
  public static async Task<T> QuerySingleAsync<T>(this NpgsqlConnection conn, string sql, object? param = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (param != null) {
      AddParameters(cmd, param);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException("Sequence contains no elements");
    }
    return MapRow<T>(reader);
  }

  /// <summary>
  /// Executes a query and returns the first row as a dynamic ExpandoObject, or null if no rows.
  /// Replaces Dapper's <c>conn.QueryFirstOrDefaultAsync&lt;dynamic&gt;(sql)</c> for existence checks.
  /// </summary>
  public static async Task<dynamic?> QueryFirstOrDefaultAsync<T>(this NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return ToDynamic(reader);
  }

  /// <summary>
  /// Executes a query and returns all rows as a list, reading the first column of each row.
  /// Replaces Dapper's <c>conn.QueryAsync&lt;string&gt;(sql)</c>.
  /// </summary>
  public static async Task<List<T>> QueryAsync<T>(this NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    var results = new List<T>();
    while (await reader.ReadAsync()) {
      var value = reader.GetValue(0);
      if (value is T typed) {
        results.Add(typed);
      } else {
        results.Add((T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture));
      }
    }
    return results;
  }

  private static T MapRow<T>(NpgsqlDataReader reader) {
    var type = typeof(T);

    // Scalar types (single column)
    if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)) {
      var value = reader.GetValue(0);
      if (value is T typed) {
        return typed;
      }
      return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    // dynamic/object → ExpandoObject
    if (type == typeof(object)) {
      return (T)(object)ToDynamic(reader);
    }

    // ValueTuple types (e.g., (int version, decimal amount, string status))
    if (type.IsValueType && type.IsGenericType && type.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true) {
      var fields = type.GetFields();
      var values = new object[fields.Length];
      for (var i = 0; i < fields.Length; i++) {
        var dbValue = reader.GetValue(i);
        if (dbValue is DBNull) {
          values[i] = default!;
        } else if (fields[i].FieldType.IsInstanceOfType(dbValue)) {
          values[i] = dbValue;
        } else {
          values[i] = Convert.ChangeType(dbValue, fields[i].FieldType, CultureInfo.InvariantCulture);
        }
      }
      return (T)Activator.CreateInstance(type, values)!;
    }

    throw new NotSupportedException($"Type {type} not supported by NpgsqlTestExtensions");
  }

  private static dynamic ToDynamic(NpgsqlDataReader reader) {
    IDictionary<string, object?> expando = new ExpandoObject();
    for (var i = 0; i < reader.FieldCount; i++) {
      expando[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
    }
    return (ExpandoObject)expando;
  }

  private static void AddParameters(NpgsqlCommand cmd, object param) {
    foreach (var prop in param.GetType().GetProperties()) {
      var value = prop.GetValue(param);
      // Convert TrackedGuid to Guid since Npgsql doesn't have a native type handler for it
      if (value is TrackedGuid trackedGuid) {
        value = (Guid)trackedGuid;
      }
      cmd.Parameters.AddWithValue(prop.Name, value ?? DBNull.Value);
    }
  }
}
