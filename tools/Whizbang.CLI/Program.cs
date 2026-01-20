using System.Globalization;
using Whizbang.Data.Dapper.Sqlite.Schema;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;
using Whizbang.Migrate.Commands;

const string version = "0.1.0";

// Parse command-line arguments
if (args.Length == 0 || args[0] == "--help" || args[0] == "-h") {
  _showHelp();
  return 0;
}

if (args[0] == "--version" || args[0] == "-v") {
  Console.WriteLine($"Whizbang CLI v{version}");
  return 0;
}

// Route to command handlers
try {
  return args[0].ToLower(CultureInfo.InvariantCulture) switch {
    "schema" => await _handleSchemaCommandAsync(args),
    "migrate" => await _handleMigrateCommandAsync(args),
    _ => throw new InvalidOperationException($"Unknown command: {args[0]}")
  };
} catch (Exception ex) {
  Console.WriteLine($"❌ Error: {ex.Message}");
  Console.WriteLine();
  Console.WriteLine("Run 'whizbang --help' for usage information.");
  return 1;
}

async Task<int> _handleSchemaCommandAsync(string[] commandArgs) {
  if (commandArgs.Length < 2) {
    Console.WriteLine("❌ Error: Missing schema subcommand");
    Console.WriteLine();
    _showSchemaHelp();
    return 1;
  }

  return commandArgs[1].ToLower(CultureInfo.InvariantCulture) switch {
    "generate" => await _generateSchemaAsync(commandArgs),
    "validate" => await _validateSchemaAsync(commandArgs),
    _ => throw new InvalidOperationException($"Unknown schema subcommand: {commandArgs[1]}")
  };
}

async Task<int> _handleMigrateCommandAsync(string[] commandArgs) {
  if (commandArgs.Length < 2 || commandArgs[1] == "--help" || commandArgs[1] == "-h") {
    _showMigrateHelp();
    return commandArgs.Length < 2 ? 1 : 0;
  }

  return commandArgs[1].ToLower(CultureInfo.InvariantCulture) switch {
    "analyze" => await _migrateAnalyzeAsync(commandArgs),
    "apply" => await _migrateApplyAsync(commandArgs),
    "status" => await _migrateStatusAsync(commandArgs),
    _ => throw new InvalidOperationException($"Unknown migrate subcommand: {commandArgs[1]}")
  };
}

async Task<int> _migrateAnalyzeAsync(string[] commandArgs) {
  // Usage: whizbang migrate analyze [--project <path>]
  var projectPath = _parseProjectPath(commandArgs, 2) ?? Environment.CurrentDirectory;

  Console.WriteLine("Whizbang Migration Analyzer");
  Console.WriteLine("===========================");
  Console.WriteLine();
  Console.WriteLine($"Analyzing: {projectPath}");
  Console.WriteLine();

  var command = new AnalyzeCommand();
  var result = await command.ExecuteAsync(projectPath);

  if (!result.Success) {
    Console.WriteLine($"❌ Error: {result.ErrorMessage}");
    return 1;
  }

  Console.WriteLine("Analysis Results:");
  Console.WriteLine($"  Wolverine handlers found:  {result.WolverineHandlerCount}");
  Console.WriteLine($"  Marten projections found:  {result.MartenProjectionCount}");
  Console.WriteLine($"  ─────────────────────────");
  Console.WriteLine($"  Total migration items:     {result.TotalMigrationItems}");
  Console.WriteLine();

  if (result.TotalMigrationItems == 0) {
    Console.WriteLine("✓ No migration patterns found. Project may already be migrated or doesn't use Marten/Wolverine.");
  } else {
    Console.WriteLine($"✓ Found {result.TotalMigrationItems} items to migrate.");
    Console.WriteLine();
    Console.WriteLine("Run 'whizbang migrate apply' to apply transformations.");
  }
  Console.WriteLine();

  return 0;
}

async Task<int> _migrateApplyAsync(string[] commandArgs) {
  // Usage: whizbang migrate apply [--project <path>] [--dry-run]
  var projectPath = _parseProjectPath(commandArgs, 2) ?? Environment.CurrentDirectory;
  var dryRun = commandArgs.Any(a => a == "--dry-run" || a == "-n");

  Console.WriteLine("Whizbang Migration Tool");
  Console.WriteLine("=======================");
  Console.WriteLine();
  Console.WriteLine($"Project: {projectPath}");
  Console.WriteLine($"Mode: {(dryRun ? "Dry run (no changes will be made)" : "Apply changes")}");
  Console.WriteLine();

  var command = new ApplyCommand();
  var result = await command.ExecuteAsync(projectPath, dryRun);

  if (!result.Success) {
    Console.WriteLine($"❌ Error: {result.ErrorMessage}");
    return 1;
  }

  if (result.TransformedFileCount == 0) {
    Console.WriteLine("✓ No files needed transformation.");
    Console.WriteLine();
    return 0;
  }

  Console.WriteLine($"Files {(dryRun ? "would be" : "")} transformed: {result.TransformedFileCount}");
  Console.WriteLine();

  foreach (var fileChange in result.Changes) {
    var relativePath = Path.GetRelativePath(projectPath, fileChange.FilePath);
    Console.WriteLine($"  {relativePath} ({fileChange.ChangeCount} changes)");
    foreach (var change in fileChange.Changes.Take(3)) {
      Console.WriteLine($"    - {change.Description}");
    }
    if (fileChange.Changes.Count > 3) {
      Console.WriteLine($"    ... and {fileChange.Changes.Count - 3} more changes");
    }
  }
  Console.WriteLine();

  if (dryRun) {
    Console.WriteLine("✓ Dry run complete. Run without --dry-run to apply changes.");
  } else {
    Console.WriteLine("✓ Migration complete!");
  }
  Console.WriteLine();

  return 0;
}

async Task<int> _migrateStatusAsync(string[] commandArgs) {
  // Usage: whizbang migrate status [--project <path>]
  var projectPath = _parseProjectPath(commandArgs, 2) ?? Environment.CurrentDirectory;

  Console.WriteLine("Whizbang Migration Status");
  Console.WriteLine("=========================");
  Console.WriteLine();
  Console.WriteLine($"Project: {projectPath}");
  Console.WriteLine();

  var command = new StatusCommand();
  var result = await command.ExecuteAsync(projectPath);

  if (!result.Success) {
    Console.WriteLine($"❌ Error: {result.ErrorMessage}");
    return 1;
  }

  Console.WriteLine($"Status: {result.Status}");

  if (result.HasActiveMigration) {
    Console.WriteLine();
    Console.WriteLine("Migration in progress:");
    Console.WriteLine($"  Checkpoints:            {result.CheckpointCount}");
    Console.WriteLine($"  Completed transformers: {result.CompletedTransformerCount}");
    Console.WriteLine($"  Pending transformers:   {result.PendingTransformerCount}");
    Console.WriteLine($"  Files transformed:      {result.TotalFilesTransformed}");
  } else if (result.Status == Whizbang.Migrate.Core.JournalStatus.Completed) {
    Console.WriteLine();
    Console.WriteLine("Migration completed:");
    Console.WriteLine($"  Total files transformed: {result.TotalFilesTransformed}");
  } else {
    Console.WriteLine();
    Console.WriteLine("No migration in progress.");
    Console.WriteLine("Run 'whizbang migrate analyze' to check for migration patterns.");
  }
  Console.WriteLine();

  return 0;
}

string? _parseProjectPath(string[] commandArgs, int startIndex) {
  for (int i = startIndex; i < commandArgs.Length; i++) {
    if (commandArgs[i] == "--project" || commandArgs[i] == "-p") {
      if (i + 1 < commandArgs.Length) {
        return commandArgs[i + 1];
      }
    }
  }
  return null;
}

async Task<int> _generateSchemaAsync(string[] commandArgs) {
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

  var database = commandArgs[2].ToLower(CultureInfo.InvariantCulture);
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
  Console.WriteLine($"Database: {database.ToUpper(CultureInfo.InvariantCulture)}");
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

async Task<int> _validateSchemaAsync(string[] commandArgs) {
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

void _showHelp() {
  Console.WriteLine("Whizbang CLI - Command-line tool for Whizbang");
  Console.WriteLine($"Version {version}");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang <command> [options]");
  Console.WriteLine();
  Console.WriteLine("Commands:");
  Console.WriteLine("  schema          Manage database schemas");
  Console.WriteLine("  migrate         Migrate from Marten/Wolverine to Whizbang");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --help, -h      Show this help message");
  Console.WriteLine("  --version, -v   Show version information");
  Console.WriteLine();
  Console.WriteLine("Run 'whizbang <command> --help' for more information on a command.");
}

void _showSchemaHelp() {
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

void _showMigrateHelp() {
  Console.WriteLine("Migration Commands");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang migrate <subcommand> [options]");
  Console.WriteLine();
  Console.WriteLine("Subcommands:");
  Console.WriteLine("  analyze              Analyze project for Marten/Wolverine patterns");
  Console.WriteLine("  apply                Apply transformations to migrate code");
  Console.WriteLine("  status               Show migration progress");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --project, -p <path>  Project directory (default: current directory)");
  Console.WriteLine("  --dry-run, -n         Preview changes without modifying files (apply only)");
  Console.WriteLine();
  Console.WriteLine("Examples:");
  Console.WriteLine("  whizbang migrate analyze");
  Console.WriteLine("  whizbang migrate analyze --project ./src/MyApp");
  Console.WriteLine("  whizbang migrate apply --dry-run");
  Console.WriteLine("  whizbang migrate apply --project ./src/MyApp");
  Console.WriteLine("  whizbang migrate status");
}
