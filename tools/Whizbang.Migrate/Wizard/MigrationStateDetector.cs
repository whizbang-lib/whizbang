namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Detects the current state of a migration for a project.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class MigrationStateDetector {
  /// <summary>
  /// Detects the current migration state for a project.
  /// </summary>
  /// <param name="projectPath">Path to the project being migrated.</param>
  /// <param name="decisionFilePath">Optional custom decision file path.</param>
  /// <returns>The detected migration state.</returns>
  public static async Task<DetectedMigrationState> DetectStateAsync(
      string projectPath,
      string? decisionFilePath = null,
      CancellationToken ct = default) {
    // Determine decision file location
    var effectivePath = decisionFilePath;
    if (string.IsNullOrEmpty(effectivePath)) {
      var projectName = await GitOperations.DeriveProjectNameAsync(projectPath, ct);
      effectivePath = DecisionFile.GetDefaultPath(projectName);
    }

    // Check if decision file exists
    if (!File.Exists(effectivePath)) {
      return new DetectedMigrationState {
        HasMigrationInProgress = false,
        DecisionFilePath = null,
        Status = MigrationStatus.NotStarted,
        ProjectPath = projectPath
      };
    }

    // Load and analyze decision file
    try {
      var decisionFile = await DecisionFile.LoadAsync(effectivePath, ct);

      var isInProgress = decisionFile.State.Status == MigrationStatus.InProgress;

      return new DetectedMigrationState {
        HasMigrationInProgress = isInProgress,
        DecisionFilePath = effectivePath,
        Status = decisionFile.State.Status,
        ProjectPath = projectPath,
        StartedAt = decisionFile.State.StartedAt,
        LastUpdatedAt = decisionFile.State.LastUpdatedAt,
        GitCommitBefore = decisionFile.State.GitCommitBefore,
        CompletedCategories = decisionFile.State.CompletedCategories.ToList(),
        CurrentCategory = decisionFile.State.CurrentCategory,
        CurrentItem = decisionFile.State.CurrentItem
      };
    } catch {
      // If we can't read the decision file, treat as no migration
      return new DetectedMigrationState {
        HasMigrationInProgress = false,
        DecisionFilePath = effectivePath,
        Status = MigrationStatus.NotStarted,
        ProjectPath = projectPath,
        Error = "Could not read decision file"
      };
    }
  }

  /// <summary>
  /// Gets a human-readable progress summary for display.
  /// </summary>
  public static string GetProgressSummary(DecisionFile decisionFile) {
    var state = decisionFile.State;

    if (state.Status == MigrationStatus.NotStarted) {
      return "Migration not started";
    }

    if (state.Status == MigrationStatus.Completed) {
      return $"Migration completed at {state.CompletedAt:g}";
    }

    if (state.Status == MigrationStatus.Reverted) {
      return "Migration was reverted";
    }

    // In progress
    var completed = state.CompletedCategories.Count > 0
        ? string.Join(", ", state.CompletedCategories)
        : "none";

    var current = state.CurrentCategory ?? "none";
    var item = state.CurrentItem;

    return $"Completed: [{completed}] | Current: {current} (item {item})";
  }
}

/// <summary>
/// Represents the detected state of a migration.
/// </summary>
public sealed class DetectedMigrationState {
  /// <summary>
  /// Whether there is a migration currently in progress.
  /// </summary>
  public bool HasMigrationInProgress { get; init; }

  /// <summary>
  /// Path to the decision file, if found.
  /// </summary>
  public string? DecisionFilePath { get; init; }

  /// <summary>
  /// Current migration status.
  /// </summary>
  public MigrationStatus Status { get; init; }

  /// <summary>
  /// Path to the project being migrated.
  /// </summary>
  public string? ProjectPath { get; init; }

  /// <summary>
  /// When the migration was started.
  /// </summary>
  public DateTimeOffset? StartedAt { get; init; }

  /// <summary>
  /// When the migration was last updated.
  /// </summary>
  public DateTimeOffset? LastUpdatedAt { get; init; }

  /// <summary>
  /// Git commit hash before migration started.
  /// </summary>
  public string? GitCommitBefore { get; init; }

  /// <summary>
  /// Categories that have been completed.
  /// </summary>
  public List<string> CompletedCategories { get; init; } = [];

  /// <summary>
  /// Current category being processed.
  /// </summary>
  public string? CurrentCategory { get; init; }

  /// <summary>
  /// Current item index in the current category.
  /// </summary>
  public int CurrentItem { get; init; }

  /// <summary>
  /// Error message if detection failed.
  /// </summary>
  public string? Error { get; init; }
}
