using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Postgres;

Console.WriteLine("Whizbang Schema Generator");
Console.WriteLine("=========================");
Console.WriteLine();

// Default configuration
var config = new SchemaConfiguration();

// Generate Postgres infrastructure schema
Console.WriteLine("Generating Postgres infrastructure schema...");
var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

// Determine output path
var outputPath = args.Length > 0
  ? args[0]
  : Path.Combine(Directory.GetCurrentDirectory(), "whizbang-schema.sql");

// Ensure output directory exists
var outputDir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir)) {
  Directory.CreateDirectory(outputDir);
}

// Write SQL to file
await File.WriteAllTextAsync(outputPath, sql);

Console.WriteLine($"✓ Generated schema SQL: {outputPath}");
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
