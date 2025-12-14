using System.Data;
using System.Reflection;
using Npgsql;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithConnectionStringOnly_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithPerspectiveSql_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithPerspectiveSql_ExecutesBothSchemasAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_PerspectiveSqlNull_SkipsPerspectiveSqlAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_PerspectiveSqlEmpty_SkipsPerspectiveSqlAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchema_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchema_WithPerspectiveSql_ExecutesBothSchemasAsync</tests>
/// Handles automatic initialization of the Whizbang PostgreSQL schema.
/// </summary>
public sealed class PostgresSchemaInitializer {
  private readonly string _connectionString;
  private readonly string? _perspectiveSchemaSql;

  public PostgresSchemaInitializer(string connectionString, string? perspectiveSchemaSql = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    _connectionString = connectionString;
    _perspectiveSchemaSql = perspectiveSchemaSql;
  }

  /// <summary>
  /// Initializes the Whizbang schema by executing the embedded whizbang-schema.sql file.
  /// Optionally executes perspective schema SQL if provided.
  /// This method is idempotent - it can be called multiple times safely.
  /// </summary>
  public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
    var schemaSql = await LoadEmbeddedSchemaAsync();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    // Execute infrastructure schema (event store, inbox, outbox, etc.)
    await using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    command.CommandTimeout = 30; // 30 seconds should be plenty for schema creation
    await command.ExecuteNonQueryAsync(cancellationToken);

    // Execute perspective schema if provided
    if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
      await using var perspectiveCommand = connection.CreateCommand();
      perspectiveCommand.CommandText = _perspectiveSchemaSql;
      perspectiveCommand.CommandTimeout = 30;
      await perspectiveCommand.ExecuteNonQueryAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Synchronous version of InitializeSchemaAsync for use during application startup.
  /// Optionally executes perspective schema SQL if provided.
  /// </summary>
  public void InitializeSchema() {
    var schemaSql = LoadEmbeddedSchema();

    using var connection = new NpgsqlConnection(_connectionString);
    connection.Open();

    // Execute infrastructure schema
    using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    command.CommandTimeout = 30;
    command.ExecuteNonQuery();

    // Execute perspective schema if provided
    if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
      using var perspectiveCommand = connection.CreateCommand();
      perspectiveCommand.CommandText = _perspectiveSchemaSql;
      perspectiveCommand.CommandTimeout = 30;
      perspectiveCommand.ExecuteNonQuery();
    }
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
