using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Postgres;
using Whizbang.Data.Schema.Sqlite;

Console.WriteLine("Whizbang Schema Generator");
Console.WriteLine("=========================");
Console.WriteLine();

// Parse arguments
// Usage: dotnet run [database] [output-path]
// Examples:
//   dotnet run postgres sql-scripts/whizbang-postgres.sql
//   dotnet run sqlite sql-scripts/whizbang-sqlite.sql
//   dotnet run (defaults to postgres, whizbang-schema.sql)
var database = args.Length > 0 ? args[0].ToLower() : "postgres";
var outputPath = args.Length > 1
  ? args[1]
  : Path.Combine(Directory.GetCurrentDirectory(), $"whizbang-{database}-schema.sql");

// Validate database type
if (database != "postgres" && database != "sqlite") {
  Console.WriteLine($"❌ Error: Unknown database type '{database}'");
  Console.WriteLine("   Supported databases: postgres, sqlite");
  Console.WriteLine();
  Console.WriteLine("Usage: dotnet run [database] [output-path]");
  Console.WriteLine("  database: postgres or sqlite (default: postgres)");
  Console.WriteLine("  output-path: path to output SQL file (default: whizbang-{database}-schema.sql)");
  return 1;
}

// Default configuration
var config = new SchemaConfiguration();

// Generate schema for specified database
Console.WriteLine($"Generating {database.ToUpper()} infrastructure schema...");
var sql = database switch {
  "postgres" => PostgresSchemaBuilder.BuildInfrastructureSchema(config),
  "sqlite" => SqliteSchemaBuilder.BuildInfrastructureSchema(config),
  _ => throw new InvalidOperationException($"Unsupported database: {database}")
};

// Ensure output directory exists
var outputDir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir)) {
  Directory.CreateDirectory(outputDir);
}

// Write SQL to file
await File.WriteAllTextAsync(outputPath, sql);

Console.WriteLine($"✓ Generated schema SQL: {outputPath}");
Console.WriteLine($"✓ Database: {database.ToUpper()}");
Console.WriteLine($"✓ Infrastructure prefix: {config.InfrastructurePrefix}");
Console.WriteLine($"✓ Perspective prefix: {config.PerspectivePrefix}");
Console.WriteLine();
Console.WriteLine("Schema includes:");
Console.WriteLine("  - wb_inbox (message deduplication)");
Console.WriteLine("  - wb_outbox (transactional messaging)");
Console.WriteLine("  - wb_event_store (event sourcing)");
Console.WriteLine("  - wb_request_response (async request/response)");
Console.WriteLine("  - wb_sequences (distributed sequences)");
Console.WriteLine();
Console.WriteLine("Done!");

return 0;
