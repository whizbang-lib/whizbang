using System.CommandLine;

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
      Console.WriteLine($"Analyzing project: {project ?? "current directory"}");
      Console.WriteLine($"Format: {format}");
      // TODO: Implement analysis
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
}
