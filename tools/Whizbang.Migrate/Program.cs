using System.CommandLine;
using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate;

/// <summary>
/// Entry point for the whizbang-migrate CLI tool.
/// </summary>
public static class Program {
  /// <summary>
  /// Main entry point.
  /// </summary>
  public static async Task<int> Main(string[] args) {
    var rootCommand = new RootCommand("Migration tool for converting Marten/Wolverine projects to Whizbang");

    // analyze command
    var analyzeCommand = new Command("analyze", "Analyze a project for migration scope");
    var projectOption = new Option<string?>(
        aliases: ["--project", "-p"],
        description: "Path to the solution or project file");
    var formatOption = new Option<string>(
        aliases: ["--format", "-f"],
        getDefaultValue: () => "table",
        description: "Output format (table or json)");

    analyzeCommand.AddOption(projectOption);
    analyzeCommand.AddOption(formatOption);
    analyzeCommand.SetHandler(async (project, format) => {
      var path = project ?? Directory.GetCurrentDirectory();
      Console.WriteLine($"Analyzing: {path}");
      Console.WriteLine();

      var wolverineAnalyzer = new WolverineAnalyzer();
      var martenAnalyzer = new MartenAnalyzer();

      // Get source directory from solution or project path
      var sourceDir = path;
      if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
          path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
        sourceDir = Path.GetDirectoryName(path) ?? path;
      }

      if (!Directory.Exists(sourceDir)) {
        Console.Error.WriteLine($"Directory not found: {sourceDir}");
        return;
      }

      // Run analyzers
      var wolverineResult = await wolverineAnalyzer.AnalyzeProjectAsync(sourceDir);
      var martenResult = await martenAnalyzer.AnalyzeProjectAsync(sourceDir);

      // Combine results
      var combinedResult = new AnalysisResult {
        Handlers = wolverineResult.Handlers,
        Projections = martenResult.Projections,
        EventStoreUsages = martenResult.EventStoreUsages,
        DIRegistrations = martenResult.DIRegistrations,
        Warnings = wolverineResult.Warnings.Concat(martenResult.Warnings).ToList()
      };

      if (format == "json") {
        Console.WriteLine("JSON output not yet implemented (requires AOT-compatible serialization)");
      } else {
        _printTableFormat(combinedResult);
      }
    }, projectOption, formatOption);

    // plan command
    var planCommand = new Command("plan", "Create a migration plan without applying changes");
    planCommand.AddOption(projectOption);
    var outputOption = new Option<string?>(
        aliases: ["--output", "-o"],
        description: "Output file for the plan");
    planCommand.AddOption(outputOption);
    planCommand.SetHandler(async (project, output) => {
      Console.WriteLine($"Planning migration for: {project ?? "current directory"}");
      // TODO: Implement planning
    }, projectOption, outputOption);

    // apply command
    var applyCommand = new Command("apply", "Apply migration transformations");
    var modeOption = new Option<string>(
        aliases: ["--mode", "-m"],
        getDefaultValue: () => "guided",
        description: "Migration mode (auto or guided)");
    applyCommand.AddOption(modeOption);
    applyCommand.SetHandler(async mode => {
      Console.WriteLine($"Applying migration in {mode} mode");
      // TODO: Implement apply
    }, modeOption);

    // rollback command
    var rollbackCommand = new Command("rollback", "Rollback to a checkpoint");
    var checkpointArgument = new Argument<string?>(
        name: "checkpoint",
        description: "Checkpoint ID to rollback to");
    var listOption = new Option<bool>(
        aliases: ["--list", "-l"],
        description: "List available checkpoints");
    rollbackCommand.AddArgument(checkpointArgument);
    rollbackCommand.AddOption(listOption);
    rollbackCommand.SetHandler(async (checkpoint, list) => {
      if (list) {
        Console.WriteLine("Available checkpoints:");
        // TODO: List checkpoints
      } else if (checkpoint != null) {
        Console.WriteLine($"Rolling back to checkpoint: {checkpoint}");
        // TODO: Implement rollback
      }
    }, checkpointArgument, listOption);

    // status command
    var statusCommand = new Command("status", "Show migration status");
    statusCommand.SetHandler(async () => {
      Console.WriteLine("Migration status:");
      // TODO: Show status
    });

    rootCommand.AddCommand(analyzeCommand);
    rootCommand.AddCommand(planCommand);
    rootCommand.AddCommand(applyCommand);
    rootCommand.AddCommand(rollbackCommand);
    rootCommand.AddCommand(statusCommand);

    return await rootCommand.InvokeAsync(args);
  }

  private static void _printTableFormat(AnalysisResult result) {
    // Summary
    Console.WriteLine("=== Migration Analysis Summary ===");
    Console.WriteLine();
    Console.WriteLine($"  Wolverine Handlers:     {result.Handlers.Count}");
    Console.WriteLine($"  Marten Projections:     {result.Projections.Count}");
    Console.WriteLine($"  Event Store Usages:     {result.EventStoreUsages.Count}");
    Console.WriteLine($"  DI Registrations:       {result.DIRegistrations.Count}");
    Console.WriteLine($"  Warnings:               {result.Warnings.Count}");
    Console.WriteLine();

    // Handlers
    if (result.Handlers.Count > 0) {
      Console.WriteLine("=== Handlers ===");
      Console.WriteLine();
      Console.WriteLine($"  {"Type",-50} {"Message",-40} {"Kind",-20}");
      Console.WriteLine($"  {new string('-', 50)} {new string('-', 40)} {new string('-', 20)}");
      foreach (var h in result.Handlers) {
        Console.WriteLine($"  {h.ClassName,-50} {h.MessageType,-40} {h.HandlerKind,-20}");
      }
      Console.WriteLine();
    }

    // Projections
    if (result.Projections.Count > 0) {
      Console.WriteLine("=== Projections ===");
      Console.WriteLine();
      Console.WriteLine($"  {"Type",-50} {"Aggregate",-40} {"Kind",-20}");
      Console.WriteLine($"  {new string('-', 50)} {new string('-', 40)} {new string('-', 20)}");
      foreach (var p in result.Projections) {
        Console.WriteLine($"  {p.ClassName,-50} {p.AggregateType,-40} {p.ProjectionKind,-20}");
      }
      Console.WriteLine();
    }

    // Event Store Usages
    if (result.EventStoreUsages.Count > 0) {
      Console.WriteLine("=== Event Store Usages ===");
      Console.WriteLine();
      Console.WriteLine($"  {"Class",-50} {"Kind",-40} {"Line",-10}");
      Console.WriteLine($"  {new string('-', 50)} {new string('-', 40)} {new string('-', 10)}");
      foreach (var e in result.EventStoreUsages) {
        Console.WriteLine($"  {e.ClassName,-50} {e.UsageKind,-40} {e.LineNumber,-10}");
      }
      Console.WriteLine();
    }

    // DI Registrations
    if (result.DIRegistrations.Count > 0) {
      Console.WriteLine("=== DI Registrations ===");
      Console.WriteLine();
      Console.WriteLine($"  {"Kind",-30} {"File",-60} {"Line",-10}");
      Console.WriteLine($"  {new string('-', 30)} {new string('-', 60)} {new string('-', 10)}");
      foreach (var d in result.DIRegistrations) {
        var shortFile = Path.GetFileName(d.FilePath);
        Console.WriteLine($"  {d.RegistrationKind,-30} {shortFile,-60} {d.LineNumber,-10}");
      }
      Console.WriteLine();
    }

    // Warnings
    if (result.Warnings.Count > 0) {
      Console.WriteLine("=== Warnings ===");
      Console.WriteLine();
      foreach (var w in result.Warnings) {
        Console.WriteLine($"  [{w.WarningKind}] {w.ClassName} (line {w.LineNumber})");
        Console.WriteLine($"    {w.Message}");
        Console.WriteLine();
      }
    }
  }
}
