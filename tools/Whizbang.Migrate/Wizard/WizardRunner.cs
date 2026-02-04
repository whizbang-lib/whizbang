namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Orchestrates the migration wizard flow.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class WizardRunner {
  /// <summary>
  /// Path to the project being migrated.
  /// </summary>
  public string ProjectPath { get; }

  /// <summary>
  /// Current wizard state.
  /// </summary>
  public WizardState State { get; }

  /// <summary>
  /// Category batches to process.
  /// </summary>
  public List<CategoryBatch> Batches { get; } = [];

  /// <summary>
  /// Total number of items across all batches.
  /// </summary>
  public int TotalItems => Batches.Sum(b => b.TotalCount);

  /// <summary>
  /// Total number of completed items across all batches.
  /// </summary>
  public int CompletedItems => Batches.Sum(b => b.CompletedCount);

  /// <summary>
  /// Whether all items are complete.
  /// </summary>
  public bool IsComplete => TotalItems > 0 && CompletedItems == TotalItems;

  /// <summary>
  /// Overall progress percentage.
  /// </summary>
  public int ProgressPercentage => TotalItems == 0 ? 0 : (CompletedItems * 100) / TotalItems;

  private WizardRunner(string projectPath, WizardState state) {
    ProjectPath = projectPath;
    State = state;
  }

  /// <summary>
  /// Creates a new wizard runner for a project.
  /// </summary>
  public static WizardRunner Create(string projectPath, string? decisionFilePath = null) {
    var state = new WizardState {
      ProjectPath = projectPath,
      Status = MigrationStatus.NotStarted,
      HasMigrationInProgress = false
    };

    return new WizardRunner(projectPath, state);
  }

  /// <summary>
  /// Adds a category batch to the runner.
  /// </summary>
  public void AddBatch(CategoryBatch batch) {
    Batches.Add(batch);
  }

  /// <summary>
  /// Gets the current batch being processed.
  /// </summary>
  /// <returns>The first incomplete batch, or null if all complete.</returns>
  public CategoryBatch? GetCurrentBatch() {
    return Batches.Find(b => !b.IsComplete);
  }

  /// <summary>
  /// Gets a batch by category.
  /// </summary>
  public CategoryBatch? GetBatch(MigrationCategory category) {
    return Batches.Find(b => b.Category == category);
  }

  /// <summary>
  /// The current decision file, if loaded or generated.
  /// </summary>
  public DecisionFile? DecisionFile { get; private set; }

  /// <summary>
  /// Generates a new decision file with defaults based on current batches.
  /// </summary>
  public DecisionFile GenerateDecisionFile() {
    var decisionFile = Wizard.DecisionFile.Create(ProjectPath);

    // Set default decisions for each category
    decisionFile.Decisions.Handlers.Default = DecisionChoice.Convert;
    decisionFile.Decisions.Projections.Default = DecisionChoice.Convert;
    decisionFile.Decisions.DiRegistration.Default = DecisionChoice.Convert;

    DecisionFile = decisionFile;
    return decisionFile;
  }

  /// <summary>
  /// Sets the decision file for this runner.
  /// </summary>
  public void SetDecisionFile(DecisionFile decisionFile) {
    DecisionFile = decisionFile;
  }

  /// <summary>
  /// Loads a decision file from a path.
  /// </summary>
  public async Task LoadDecisionFileAsync(string path, CancellationToken ct = default) {
    DecisionFile = await Wizard.DecisionFile.LoadAsync(path, ct);
  }

  /// <summary>
  /// Saves the current decision file to a path.
  /// </summary>
  public async Task SaveDecisionFileAsync(string path, CancellationToken ct = default) {
    if (DecisionFile is null) {
      throw new InvalidOperationException("No decision file to save. Call GenerateDecisionFile or LoadDecisionFileAsync first.");
    }

    await DecisionFile.SaveAsync(path, ct: ct);
  }

  /// <summary>
  /// Gets the decision for a specific migration item.
  /// </summary>
  public DecisionChoice GetDecisionForItem(MigrationItem item, MigrationCategory category) {
    if (DecisionFile is null) {
      return DecisionChoice.Prompt;
    }

    return category switch {
      MigrationCategory.Handlers => DecisionFile.GetHandlerDecision(item.FilePath),
      MigrationCategory.Projections => DecisionFile.GetProjectionDecision(item.FilePath),
      _ => DecisionChoice.Prompt
    };
  }
}

/// <summary>
/// Current state of the wizard.
/// </summary>
public sealed class WizardState {
  /// <summary>
  /// Path to the project being migrated.
  /// </summary>
  public string? ProjectPath { get; init; }

  /// <summary>
  /// Current migration status.
  /// </summary>
  public MigrationStatus Status { get; set; }

  /// <summary>
  /// Whether there is a migration in progress.
  /// </summary>
  public bool HasMigrationInProgress { get; set; }

  /// <summary>
  /// When the migration was started.
  /// </summary>
  public DateTimeOffset? StartedAt { get; set; }

  /// <summary>
  /// Git commit hash before migration started.
  /// </summary>
  public string? GitCommitBefore { get; set; }

  /// <summary>
  /// Path to the decision file.
  /// </summary>
  public string? DecisionFilePath { get; set; }
}
