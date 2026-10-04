using System.Globalization;
using Whizbang.Core.Diagnostics;
using Whizbang.Data.Dapper.Sqlite.Schema;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;
using Whizbang.Migrate.Commands;

const string version = "0.1.0";

// Show branded banner with tool info
WhizbangBanner.PrintHeader("Whizbang CLI", version);

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
    "stored-forms" => await _handleStoredFormsCommandAsync(args),
    "streams" => await _handleStreamsCommandAsync(args),
    "redeliver" => await _handleRedeliverCommandAsync(args),
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

async Task<int> _handleStoredFormsCommandAsync(string[] commandArgs) {
  // Usage: whizbang stored-forms status --connection <connection string> [--schema <schema>]
  if (commandArgs.Length < 2 || commandArgs[1] is "--help" or "-h") {
    _showStoredFormsHelp();
    return commandArgs.Length < 2 ? 1 : 0;
  }
  if (!string.Equals(commandArgs[1], "status", StringComparison.OrdinalIgnoreCase)) {
    throw new InvalidOperationException($"Unknown stored-forms subcommand: {commandArgs[1]}");
  }

  var connectionString = _option(commandArgs, "--connection", "-c");
  if (string.IsNullOrWhiteSpace(connectionString)) {
    Console.WriteLine("❌ Error: Missing --connection");
    Console.WriteLine();
    _showStoredFormsHelp();
    return 1;
  }
  var schema = _option(commandArgs, "--schema", "-s") ?? "public";

  await using var connection = new Npgsql.NpgsqlConnection(connectionString);
  await connection.OpenAsync();
  var statuses = await Whizbang.Data.Postgres.StoredFormMigrationJournal.ReadAsync(connection, schema);
  Console.WriteLine($"Stored-form migrations in schema {schema}");
  Console.WriteLine();
  Console.WriteLine(Whizbang.Data.Postgres.StoredFormMigrationJournal.Format(statuses));
  return 0;
}

async Task<int> _handleStreamsCommandAsync(string[] commandArgs) {
  // Usage: whizbang streams purge --connection <cs> [--schema <schema>] --reason <text> [--requested-by <name>]
  //        (--stream <id> ... | --streams-file <path>) [--dry-run] [--batch-size <n>] [--purge-id <guid>]
  if (commandArgs.Length < 2 || commandArgs[1] is "--help" or "-h") {
    _showStreamsHelp();
    return commandArgs.Length < 2 ? 1 : 0;
  }
  if (!string.Equals(commandArgs[1], "purge", StringComparison.OrdinalIgnoreCase)) {
    throw new InvalidOperationException($"Unknown streams subcommand: {commandArgs[1]}");
  }

  var connectionString = _option(commandArgs, "--connection", "-c");
  var reason = _option(commandArgs, "--reason", "-r");
  var streamIds = new List<Guid>();
  foreach (var value in _options(commandArgs, "--stream")) {
    streamIds.Add(Guid.Parse(value, CultureInfo.InvariantCulture));
  }
  var streamsFile = _option(commandArgs, "--streams-file", "-f");
  if (streamsFile is not null) {
    foreach (var line in await File.ReadAllLinesAsync(streamsFile)) {
      var trimmed = line.Trim();
      if (trimmed.Length > 0 && !trimmed.StartsWith('#')) {
        streamIds.Add(Guid.Parse(trimmed, CultureInfo.InvariantCulture));
      }
    }
  }
  if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(reason) || streamIds.Count == 0) {
    Console.WriteLine("❌ Error: --connection, --reason and at least one stream (--stream or --streams-file) are required");
    Console.WriteLine();
    _showStreamsHelp();
    return 1;
  }

  var request = new Whizbang.Core.Messaging.StreamPurgeRequest {
    StreamIds = streamIds,
    Reason = reason,
    RequestedBy = _option(commandArgs, "--requested-by", "-u") ?? Environment.UserName,
    DryRun = commandArgs.Contains("--dry-run"),
    BatchSize = _option(commandArgs, "--batch-size", "-b") is { } size
      ? int.Parse(size, CultureInfo.InvariantCulture)
      : Whizbang.Core.Messaging.StreamPurgeRequest.DEFAULT_BATCH_SIZE,
  };
  if (_option(commandArgs, "--purge-id", "-p") is { } purgeId) {
    request = request with { PurgeId = Guid.Parse(purgeId, CultureInfo.InvariantCulture) };
  }

  await using var connection = new Npgsql.NpgsqlConnection(connectionString);
  await connection.OpenAsync();
  var report = await Whizbang.Data.Postgres.PostgresStreamPurger.RunAsync(
    connection, _option(commandArgs, "--schema", "-s") ?? "public", request);
  Console.WriteLine(report.Format());
  return 0;
}

IEnumerable<string> _options(string[] commandArgs, string name) {
  for (var i = 2; i < commandArgs.Length - 1; i++) {
    if (commandArgs[i] == name) {
      yield return commandArgs[i + 1];
    }
  }
}

void _showStreamsHelp() {
  Console.WriteLine("Stream Commands");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang streams purge [options]");
  Console.WriteLine();
  Console.WriteLine("Removes durable streams that should never have existed from one service's store: events, every");
  Console.WriteLine("perspective's rows, perspective work, cursors and snapshots, outbox, inbox and deduplication entries.");
  Console.WriteLine("One transaction per batch; each batch is audited, and the streams stay purged: a later event on one");
  Console.WriteLine("is skipped by every perspective. Stop whatever produces events for the streams first.");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --connection, -c <string>   PostgreSQL connection string of the service's database");
  Console.WriteLine("  --schema, -s <name>         The schema the service uses (default: public)");
  Console.WriteLine("  --reason, -r <text>         Why, recorded in the audit (required)");
  Console.WriteLine("  --requested-by, -u <name>   Who, recorded in the audit (default: the OS user)");
  Console.WriteLine("  --stream <id>               A stream to purge (repeatable)");
  Console.WriteLine("  --streams-file, -f <path>   A file of stream ids, one per line ('#' comments)");
  Console.WriteLine("  --dry-run                   Report the rows per table that would go, and change nothing");
  Console.WriteLine($"  --batch-size, -b <n>        Streams per transaction (default: {Whizbang.Core.Messaging.StreamPurgeRequest.DEFAULT_BATCH_SIZE})");
  Console.WriteLine("  --purge-id, -p <guid>       Resume an earlier purge: batches it committed are skipped");
  Console.WriteLine();
  Console.WriteLine("Example:");
  Console.WriteLine("  whizbang streams purge -c \"Host=...;Database=...;Username=...\" -r \"orphaned by a replay\" -f ids.txt --dry-run");
}

async Task<int> _handleRedeliverCommandAsync(string[] commandArgs) {
  // Usage: whizbang redeliver --service <url> --origin <name> (--streams <id,id,...> | --streams-file <path>) [options]
  // Posts to the receiving service's operator endpoint (MapWhizbangStreamRedeliveryEndpoints), which asks the origin
  // to republish the streams' stored events; the receiving service dedupes them by event id and re-applies them.
  if (commandArgs.Length < 2 || commandArgs[1] is "--help" or "-h") {
    _showRedeliverHelp();
    return commandArgs.Length < 2 ? 1 : 0;
  }
  var service = _flag(commandArgs, "--service");
  var origin = _flag(commandArgs, "--origin");
  var streams = new List<string>();
  if (_flag(commandArgs, "--streams") is { } inline) {
    streams.AddRange(inline.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
  }
  if (_flag(commandArgs, "--streams-file") is { } file) {
    streams.AddRange((await File.ReadAllLinesAsync(file))
      .SelectMany(line => line.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
  }
  if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(origin) || streams.Count == 0) {
    Console.WriteLine("❌ Error: --service, --origin and at least one stream (--streams or --streams-file) are required");
    Console.WriteLine();
    _showRedeliverHelp();
    return 1;
  }
  var ids = new System.Text.Json.Nodes.JsonArray();
  foreach (var stream in streams) {
    if (!Guid.TryParse(stream, out var id)) {
      Console.WriteLine($"❌ Error: '{stream}' is not a stream id");
      return 1;
    }
    ids.Add((System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id.ToString()));
  }
  var body = new System.Text.Json.Nodes.JsonObject {
    ["originService"] = origin,
    ["streamIds"] = ids,
    ["originRequestTopic"] = _flag(commandArgs, "--origin-topic"),
    ["replyTopic"] = _flag(commandArgs, "--reply-topic"),
    ["tenantScope"] = _flag(commandArgs, "--tenant"),
    ["stateOnly"] = commandArgs.Contains("--state-only"),
  };
  if (_flag(commandArgs, "--event-types") is { } types) {
    var array = new System.Text.Json.Nodes.JsonArray();
    foreach (var type in types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      array.Add((System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(type));
    }
    body["eventTypes"] = array;
  }
  var path = _flag(commandArgs, "--path") ?? "/whizbang/redelivery";
  using var http = new HttpClient { BaseAddress = new Uri(service) };
  if (_flag(commandArgs, "--bearer") is { } token) {
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
  }
  using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
  using var response = await http.PostAsync(path.TrimEnd('/') + "/streams", content);
  var text = await response.Content.ReadAsStringAsync();
  if (!response.IsSuccessStatusCode) {
    Console.WriteLine($"❌ Error: {(int)response.StatusCode} {response.ReasonPhrase}: {text}");
    return 1;
  }
  Console.WriteLine($"✅ Redelivery requested: {text}");
  return 0;
}

string? _flag(string[] commandArgs, string name) {
  for (var i = 1; i < commandArgs.Length - 1; i++) {
    if (commandArgs[i] == name) {
      return commandArgs[i + 1];
    }
  }
  return null;
}

void _showRedeliverHelp() {
  Console.WriteLine("Redeliver Command");
  Console.WriteLine();
  Console.WriteLine("Asks an origin service to republish the stored events of a list of streams to the service that lost them.");
  Console.WriteLine("The receiving service skips the events it already has, by id, and applies the rest.");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang redeliver --service <url> --origin <name> (--streams <ids> | --streams-file <path>) [options]");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --service <url>          Base URL of the RECEIVING service, which mounts MapWhizbangStreamRedeliveryEndpoints");
  Console.WriteLine("  --origin <name>          The origin service's logical name");
  Console.WriteLine("  --streams <ids>          Stream ids, separated by commas or spaces");
  Console.WriteLine("  --streams-file <path>    A file of stream ids, one or more per line");
  Console.WriteLine("  --origin-topic <topic>   The topic the origin takes requests on, when not learned from its checkpoints");
  Console.WriteLine("  --reply-topic <topic>    The topic to republish on (default: the receiving service's repair or inbox topic)");
  Console.WriteLine("  --tenant <id>            Only events of this tenant");
  Console.WriteLine("  --event-types <a,b>      Only these stored event types");
  Console.WriteLine("  --state-only             Store and project only; do not run trigger receptors again");
  Console.WriteLine("  --path <path>            The endpoint's prefix (default: /whizbang/redelivery)");
  Console.WriteLine("  --bearer <token>         A bearer token for an endpoint that requires authorization");
  Console.WriteLine();
  Console.WriteLine("Example:");
  Console.WriteLine("  whizbang redeliver --service https://receiver.internal --origin order-service --streams-file lost-streams.txt");
}

string? _option(string[] commandArgs, string name, string alias) {
  for (var i = 2; i < commandArgs.Length - 1; i++) {
    if (commandArgs[i] == name || commandArgs[i] == alias) {
      return commandArgs[i + 1];
    }
  }
  return null;
}

void _showStoredFormsHelp() {
  Console.WriteLine("Stored-Form Migration Commands");
  Console.WriteLine();
  Console.WriteLine("Usage: whizbang stored-forms <subcommand> [options]");
  Console.WriteLine();
  Console.WriteLine("Subcommands:");
  Console.WriteLine("  status    List the stored-form migrations a schema's journal records: Pending, Applied or Settled");
  Console.WriteLine();
  Console.WriteLine("Options:");
  Console.WriteLine("  --connection, -c <string>   PostgreSQL connection string of the environment");
  Console.WriteLine("  --schema, -s <name>         The schema the application uses (default: public)");
  Console.WriteLine();
  Console.WriteLine("Example:");
  Console.WriteLine("  whizbang stored-forms status --connection \"Host=...;Database=...;Username=...\" --schema public");
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
  Console.WriteLine("  ─────────────────────────");
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
  // Usage: whizbang migrate apply [--project <path>] [--dry-run] [--guided]
  var projectPath = _parseProjectPath(commandArgs, 2) ?? Environment.CurrentDirectory;
  var dryRun = commandArgs.Any(a => a == "--dry-run" || a == "-n");
  var guided = commandArgs.Any(a => a == "--guided" || a == "-g");

  Console.WriteLine("Whizbang Migration Tool");
  Console.WriteLine("=======================");
  Console.WriteLine();
  Console.WriteLine($"Project: {projectPath}");
  string modeDesc;
  if (dryRun) {
    modeDesc = "Dry run";
  } else if (guided) {
    modeDesc = "Guided (interactive)";
  } else {
    modeDesc = "Apply all";
  }
  Console.WriteLine($"Mode: {modeDesc}");
  Console.WriteLine();

  // In guided mode, first do a dry run to show what would change
  var command = new ApplyCommand();
  var previewResult = await command.ExecuteAsync(projectPath, dryRun: true);

  if (!previewResult.Success) {
    Console.WriteLine($"❌ Error: {previewResult.ErrorMessage}");
    return 1;
  }

  if (previewResult.TransformedFileCount == 0) {
    Console.WriteLine("✓ No files needed transformation.");
    Console.WriteLine();
    return 0;
  }

  Console.WriteLine($"Files to transform: {previewResult.TransformedFileCount}");
  Console.WriteLine();

  if (guided && !dryRun) {
    // Guided mode: approve each file
    var approvedFiles = new List<string>();

    foreach (var fileChange in previewResult.Changes) {
      var relativePath = Path.GetRelativePath(projectPath, fileChange.FilePath);
      Console.WriteLine($"┌─ {relativePath} ({fileChange.ChangeCount} changes)");

      foreach (var change in fileChange.Changes) {
        Console.WriteLine($"│  • {change.Description}");
        if (!string.IsNullOrEmpty(change.OriginalText) && !string.IsNullOrEmpty(change.NewText)) {
          Console.WriteLine($"│    - {change.OriginalText}");
          Console.WriteLine($"│    + {change.NewText}");
        }
      }

      Console.WriteLine("└─");
      Console.Write("  Apply changes to this file? [y/n/a(ll)/q(uit)]: ");

      var response = Console.ReadLine()?.Trim().ToLower(System.Globalization.CultureInfo.InvariantCulture) ?? "";

      if (response == "q" || response == "quit") {
        Console.WriteLine();
        Console.WriteLine("Migration aborted by user.");
        return 1;
      }

      if (response == "a" || response == "all") {
        // Apply all remaining files
        approvedFiles.Add(fileChange.FilePath);
        foreach (var remaining in previewResult.Changes.SkipWhile(c => c.FilePath != fileChange.FilePath).Skip(1)) {
          approvedFiles.Add(remaining.FilePath);
        }
        break;
      }

      if (response == "y" || response == "yes") {
        approvedFiles.Add(fileChange.FilePath);
      }

      Console.WriteLine();
    }

    if (approvedFiles.Count == 0) {
      Console.WriteLine("No files were approved for transformation.");
      return 0;
    }

    // Apply only approved files
    Console.WriteLine();
    Console.WriteLine($"Applying changes to {approvedFiles.Count} file(s)...");

    var appliedCount = 0;
    foreach (var filePath in approvedFiles) {
      var sourceCode = await File.ReadAllTextAsync(filePath);
      var transformResult = await _applyAllTransformersAsync(sourceCode, filePath);

      if (transformResult.Changes.Count > 0) {
        await File.WriteAllTextAsync(filePath, transformResult.TransformedCode);
        appliedCount++;
      }
    }

    Console.WriteLine();
    Console.WriteLine($"✓ Migration complete! {appliedCount} file(s) transformed.");
  } else {
    // Non-guided mode: show preview and apply all (or just preview for dry-run)
    foreach (var fileChange in previewResult.Changes) {
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
      // Actually apply the changes
      var result = await command.ExecuteAsync(projectPath, dryRun: false);
      Console.WriteLine($"✓ Migration complete! {result.TransformedFileCount} file(s) transformed.");
    }
  }
  Console.WriteLine();

  return 0;
}

async Task<Whizbang.Migrate.Transformers.TransformationResult> _applyAllTransformersAsync(
    string sourceCode,
    string filePath) {
  var handlerTransformer = new Whizbang.Migrate.Transformers.HandlerToReceptorTransformer();
  var projectionTransformer = new Whizbang.Migrate.Transformers.ProjectionToPerspectiveTransformer();
  var diTransformer = new Whizbang.Migrate.Transformers.DIRegistrationTransformer();

  var allChanges = new List<Whizbang.Migrate.Transformers.CodeChange>();
  var transformedCode = sourceCode;

  var handlerResult = await handlerTransformer.TransformAsync(transformedCode, filePath);
  if (handlerResult.Changes.Count > 0) {
    transformedCode = handlerResult.TransformedCode;
    allChanges.AddRange(handlerResult.Changes);
  }

  var projectionResult = await projectionTransformer.TransformAsync(transformedCode, filePath);
  if (projectionResult.Changes.Count > 0) {
    transformedCode = projectionResult.TransformedCode;
    allChanges.AddRange(projectionResult.Changes);
  }

  var diResult = await diTransformer.TransformAsync(transformedCode, filePath);
  if (diResult.Changes.Count > 0) {
    transformedCode = diResult.TransformedCode;
    allChanges.AddRange(diResult.Changes);
  }

  return new Whizbang.Migrate.Transformers.TransformationResult(
      sourceCode,
      transformedCode,
      allChanges,
      []);
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
    if ((commandArgs[i] == "--project" || commandArgs[i] == "-p") && i + 1 < commandArgs.Length) {
      return commandArgs[i + 1];
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

  var argIndex = 3;
  while (argIndex < commandArgs.Length) {
    if ((commandArgs[argIndex] == "--output" || commandArgs[argIndex] == "-o") && argIndex + 1 < commandArgs.Length) {
      outputPath = commandArgs[argIndex + 1];
      argIndex += 2;
    } else if (commandArgs[argIndex] == "--prefix" && argIndex + 1 < commandArgs.Length) {
      prefix = commandArgs[argIndex + 1];
      argIndex += 2;
    } else {
      argIndex++;
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
  foreach (var table in requiredTables
      .Where(t => !sql.Contains("_" + t, StringComparison.OrdinalIgnoreCase))) {
    errors.Add($"Missing required table: {table}");
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
  Console.WriteLine("  stored-forms    List pending and applied stored-form migrations of perspective data");
  Console.WriteLine("  streams         Purge durable streams from a service's store (dry run, audited)");
  Console.WriteLine("  redeliver       Ask an origin service to republish the stored events of a list of streams");
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
  Console.WriteLine("  --guided, -g          Interactive mode: approve each file (apply only)");
  Console.WriteLine();
  Console.WriteLine("Examples:");
  Console.WriteLine("  whizbang migrate analyze");
  Console.WriteLine("  whizbang migrate analyze --project ./src/MyApp");
  Console.WriteLine("  whizbang migrate apply --dry-run");
  Console.WriteLine("  whizbang migrate apply --guided");
  Console.WriteLine("  whizbang migrate apply --project ./src/MyApp");
  Console.WriteLine("  whizbang migrate status");
}
