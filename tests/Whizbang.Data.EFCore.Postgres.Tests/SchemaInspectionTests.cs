using Npgsql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests to inspect the actual database schema created by EF Core.
/// Used to debug column naming issues.
/// </summary>
public class SchemaInspectionTests : EFCoreTestBase {
  [Test]
  public async Task InspectPerspectiveTableSchema_ShouldShowActualColumnNamesAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query the information_schema to see what columns exist
    var sql = @"
      SELECT column_name, data_type
      FROM information_schema.columns
      WHERE table_name = 'wh_per_order'
      ORDER BY ordinal_position";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var columns = new List<(string Name, string Type)>();
    while (await reader.ReadAsync()) {
      columns.Add((reader.GetString(0), reader.GetString(1)));
    }

    // Assert - Log what we found
    Console.WriteLine("=== wh_per_order table columns ===");
    foreach (var (name, type) in columns) {
      Console.WriteLine($"  {name} ({type})");
    }

    await Assert.That(columns.Count).IsGreaterThan(0);

    // Check if we have lowercase 'version' or PascalCase 'Version'
    var hasLowercaseVersion = columns.Any(c => c.Name == "version");
    var hasPascalCaseVersion = columns.Any(c => c.Name == "Version");

    Console.WriteLine($"\nHas lowercase 'version': {hasLowercaseVersion}");
    Console.WriteLine($"Has PascalCase 'Version': {hasPascalCaseVersion}");

    // We expect lowercase based on our HasColumnName() mappings
    await Assert.That(hasLowercaseVersion).IsTrue();
  }

  [Test]
  public async Task InspectAllWhizbangTables_ShouldShowSchemaAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Get all Whizbang tables
    var sql = @"
      SELECT table_name
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'
      ORDER BY table_name";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var tables = new List<string>();
    while (await reader.ReadAsync()) {
      tables.Add(reader.GetString(0));
    }

    // Assert
    Console.WriteLine("=== Whizbang tables ===");
    foreach (var table in tables) {
      Console.WriteLine($"  {table}");
    }

    await Assert.That(tables).Contains("wh_per_order");
    await Assert.That(tables).Contains("wh_inbox");
    await Assert.That(tables).Contains("wh_outbox");
    await Assert.That(tables).Contains("wh_events");
  }
}
