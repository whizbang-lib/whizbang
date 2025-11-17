using System.Data;
using System.Reflection;
using Npgsql;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Handles automatic initialization of the Whizbang PostgreSQL schema.
/// </summary>
public sealed class PostgresSchemaInitializer {
  private readonly string _connectionString;

  public PostgresSchemaInitializer(string connectionString) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    _connectionString = connectionString;
  }

  /// <summary>
  /// Initializes the Whizbang schema by executing the embedded whizbang-schema.sql file.
  /// This method is idempotent - it can be called multiple times safely.
  /// </summary>
  public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
    var schemaSql = await LoadEmbeddedSchemaAsync();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    await using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    command.CommandTimeout = 30; // 30 seconds should be plenty for schema creation

    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>
  /// Synchronous version of InitializeSchemaAsync for use during application startup.
  /// </summary>
  public void InitializeSchema() {
    var schemaSql = LoadEmbeddedSchema();

    using var connection = new NpgsqlConnection(_connectionString);
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    command.CommandTimeout = 30;

    command.ExecuteNonQuery();
  }

  private static async Task<string> LoadEmbeddedSchemaAsync() {
    var assembly = Assembly.GetExecutingAssembly();
    var resourceName = "Whizbang.Data.Dapper.Postgres.whizbang-schema.sql";

    await using var stream = assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found. Ensure whizbang-schema.sql is set as an embedded resource.");

    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync();
  }

  private static string LoadEmbeddedSchema() {
    var assembly = Assembly.GetExecutingAssembly();
    var resourceName = "Whizbang.Data.Dapper.Postgres.whizbang-schema.sql";

    using var stream = assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found. Ensure whizbang-schema.sql is set as an embedded resource.");

    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }
}
