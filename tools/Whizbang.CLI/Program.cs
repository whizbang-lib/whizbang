using Whizbang.Data.Dapper.Sqlite.Schema;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;

const string version = "0.1.0";

// Parse command-line arguments
if (args.Length == 0 || args[0] == "--help" || args[0] == "-h") {
  ShowHelp();
  return 0;
}

if (args[0] == "--version" || args[0] == "-v") {
  Console.WriteLine($"Whizbang CLI v{version}");
  return 0;
}

// Route to command handlers
try {
  return args[0].ToLower() switch {
    "schema" => await HandleSchemaCommandAsync(args),
    _ => throw new InvalidOperationException($"Unknown command: {args[0]}")
  };
} catch (Exception ex) {
  Console.WriteLine($"❌ Error: {ex.Message}");
  Console.WriteLine();
  Console.WriteLine("Run 'whizbang --help' for usage information.");
  return 1;
}

async Task<int> HandleSchemaCommandAsync(string[] commandArgs) {
  if (commandArgs.Length < 2) {
    Console.WriteLine("❌ Error: Missing schema subcommand");
    Console.WriteLine();
    ShowSchemaHelp();
    return 1;
  }

  return commandArgs[1].ToLower() switch {
    "generate" => await GenerateSchemaAsync(commandArgs),
    "validate" => await ValidateSchemaAsync(commandArgs),
    _ => throw new InvalidOperationException($"Unknown schema subcommand: {commandArgs[1]}")
  };
}

async Task<int> GenerateSchemaAsync(string[] commandArgs) {
  // Usage: whizbang schema generate <database> [--output <path>] [--prefix <prefix>]
  if (commandArgs.Length < 3) {
    Console.WriteLine("❌ Error: Missing database type");
    Console.WriteLine();
    Console.WriteLine("Usage: whizbang schema generate <database> [options]");
    Console.WriteLine("  database: postgres | sqlite");
    Console.WriteLine("Options:");
    Console.WriteLine("  --output, -o <path>    Output file path");
    Console.WriteLine("  --prefix <prefix>      Infrastructure table prefix (default: wb_)");
    return 1;
  }

  var database = commandArgs[2].ToLower();
  if (database != "postgres" && database != "sqlite") {
    Console.WriteLine($"❌ Error: Unknown database type '{database}'");
    Console.WriteLine("   Supported databases: postgres, sqlite");
    return 1;
  }

  // Parse options
  string? outputPath = null;
  string? prefix = null;

  for (int i = 3; i < commandArgs.Length; i++) {
    if (commandArgs[i] == "--output" || commandArgs[i] == "-o") {
      if (i + 1 < commandArgs.Length) {
        outputPath = commandArgs[++i];
      }
    } else if (commandArgs[i] == "--prefix") {
      if (i + 1 < commandArgs.Length) {
        prefix = commandArgs[++i];
      }
    }
  }

  outputPath ??= $"whizbang-{database}-schema.sql";

  // Create configuration
  var config = prefix != null
    ? new SchemaConfiguration(InfrastructurePrefix: prefix)
    : new SchemaConfiguration();

  Console.WriteLine("Whizbang Schema Generator");
  Console.WriteLine("=========================");
  Console.WriteLine();
  Console.WriteLine($"Database: {database.ToUpper()}");
  Console.WriteLine($"Output: {outputPath}");
  Console.WriteLine($"Infrastructure Prefix: {config.InfrastructurePrefix}");
  Console.WriteLine($"Perspective Prefix: {config.PerspectivePrefix}");
  Console.WriteLine();

  // Generate schema
  var sql = database switch {
    "postgres" => PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config),
    "sqlite" => SqliteSchemaBuilder.Instance.BuildInfrastructureSchema(config),
    _ => throw new InvalidOperationException($"Unsupported database: {database}")
  };

  // Ensure output directory exists
  var outputDir = Path.GetDirectoryName(outputPath);
  if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir)) {
    Directory.CreateDirectory(outputDir);
  }

  // Write SQL to file
  await File.WriteAllTextAsync(outputPath, sql);

  Console.WriteLine("✓ Schema generated successfully!");
  Console.WriteLine();
  Console.WriteLine("Schema includes:");
  Console.WriteLine("  - wb_inbox (message deduplication)");
  Console.WriteLine("  - wb_outbox (transactional messaging)");
  Console.WriteLine("  - wb_event_store (event sourcing)");
  Console.WriteLine("  - wb_request_response (async request/response)");
  Console.WriteLine("  - wb_sequences (distributed sequences)");
  Console.WriteLine();

  return 0;
}

async Task<int> ValidateSchemaAsync(string[] commandArgs) {
  // Usage: whizbang schema validate <file>
  if (commandArgs.Length < 3) {
    Console.WriteLine("❌ Error: Missing schema file path");
    Console.WriteLine();
    Console.WriteLine("Usage: whizbang schema validate <file>");
    return 1;
  }

  var filePath = commandArgs[2];

  if (!File.Exists(filePath)) {
    Console.WriteLine($"❌ Error: File not found: {filePath}");
    return 1;
  }

  Console.WriteLine("Whizbang Schema Validator");
  Console.WriteLine("=========================");
  Console.WriteLine();
  Console.WriteLine($"Validating: {filePath}");
  Console.WriteLine();

  var sql = await File.ReadAllTextAsync(filePath);

  // Basic validation checks
  var errors = new List<string>();

  // Check for required tables
  var requiredTables = new[] { "inbox", "outbox", "event_store", "request_response", "sequences" };
  foreach (var table in requiredTables) {
    if (!sql.Contains($"_" + table, StringComparison.OrdinalIgnoreCase)) {
      errors.Add($"Missing required table: {table}");
    }
  }

  // Check for CREATE TABLE statements
  if (!sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) {
    errors.Add("No CREATE TABLE statements found");
  }

  if (errors.Count > 0) {
    Console.WriteLine("❌ Validation failed:");
    foreach (var error in errors) {
      Console.WriteLine($"   - {error}");
    }
    return 1;
  }

  Console.WriteLine("✓ Schema validation passed!");
  Console.WriteLine();
  Console.WriteLine($"Found {requiredTables.Length} required tables");
  Console.WriteLine("All basic validation checks passed");
  Console.WriteLine();

  return 0;
}

void ShowHelp() {
  Console.WriteLine("Whizbang CLI - Command-line tool for Whizbang");
  Console.WriteLine($"Version {version}");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang <command> [options]");
  Console.WriteLine();
  Console.WriteLine("Commands:");
  Console.WriteLine("  schema          Manage database schemas");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --help, -h      Show this help message");
  Console.WriteLine("  --version, -v   Show version information");
  Console.WriteLine();
  Console.WriteLine("Run 'whizbang schema --help' for more information on schema commands.");
}

void ShowSchemaHelp() {
  Console.WriteLine("Schema Management Commands");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang schema <subcommand> [options]");
  Console.WriteLine();
  Console.WriteLine("Subcommands:");
  Console.WriteLine("  generate <database>    Generate schema DDL for a database");
  Console.WriteLine("  validate <file>        Validate a schema SQL file");
  Console.WriteLine();
  Console.WriteLine("Examples:");
  Console.WriteLine("  whizbang schema generate postgres");
  Console.WriteLine("  whizbang schema generate sqlite --output my-schema.sql");
  Console.WriteLine("  whizbang schema generate postgres --prefix custom_");
  Console.WriteLine("  whizbang schema validate whizbang-postgres-schema.sql");
}
