using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Migrate.Wizard;

/// <summary>
/// JSON serialization context for AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DecisionFile))]
internal sealed partial class DecisionFileJsonContext : JsonSerializerContext { }

/// <summary>
/// Represents a migration decision file that stores all migration choices and state.
/// Decision files can be stored outside the working tree to survive git operations.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class DecisionFile {

  /// <summary>
  /// Version of the decision file format.
  /// </summary>
  public string Version { get; set; } = "1.0";

  /// <summary>
  /// Path to the project being migrated.
  /// </summary>
  public string ProjectPath { get; set; } = string.Empty;

  /// <summary>
  /// When this decision file was created.
  /// </summary>
  public DateTimeOffset GeneratedAt { get; set; }

  /// <summary>
  /// Current migration state tracking.
  /// </summary>
  public MigrationState State { get; set; } = new();

  /// <summary>
  /// All migration decisions by category.
  /// </summary>
  public MigrationDecisions Decisions { get; set; } = new();

  /// <summary>
  /// Creates a new decision file for a project.
  /// </summary>
  public static DecisionFile Create(string projectPath) {
    return new DecisionFile {
      ProjectPath = projectPath,
      GeneratedAt = DateTimeOffset.UtcNow,
      State = new MigrationState {
        Status = MigrationStatus.NotStarted
      },
      Decisions = new MigrationDecisions()
    };
  }

  /// <summary>
  /// Serializes the decision file to JSON.
  /// </summary>
  public string ToJson() {
    return JsonSerializer.Serialize(this, DecisionFileJsonContext.Default.DecisionFile);
  }

  /// <summary>
  /// Deserializes a decision file from JSON.
  /// </summary>
  public static DecisionFile FromJson(string json) {
    return JsonSerializer.Deserialize(json, DecisionFileJsonContext.Default.DecisionFile)
        ?? throw new InvalidOperationException("Failed to deserialize decision file");
  }

  /// <summary>
  /// Saves the decision file to a path.
  /// </summary>
  public async Task SaveAsync(string path, CancellationToken ct = default) {
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
      Directory.CreateDirectory(directory);
    }

    var json = ToJson();
    await File.WriteAllTextAsync(path, json, ct);
  }

  /// <summary>
  /// Loads a decision file from a path.
  /// </summary>
  public static async Task<DecisionFile> LoadAsync(string path, CancellationToken ct = default) {
    var json = await File.ReadAllTextAsync(path, ct);
    return FromJson(json);
  }

  /// <summary>
  /// Gets the default storage path for a project's decision file.
  /// Default: ~/.whizbang/migrations/{projectName}/decisions.json
  /// </summary>
  public static string GetDefaultPath(string projectName) {
    var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(userHome, ".whizbang", "migrations", projectName, "decisions.json");
  }

  /// <summary>
  /// Updates the migration state with automatic timestamp update.
  /// </summary>
  public void UpdateState(Action<MigrationState> update) {
    update(State);
    State.LastUpdatedAt = DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Marks a category as complete and moves to the next.
  /// </summary>
  public void MarkCategoryComplete(string completedCategory, string? nextCategory) {
    UpdateState(state => {
      if (!state.CompletedCategories.Contains(completedCategory)) {
        state.CompletedCategories.Add(completedCategory);
      }
      state.CurrentCategory = nextCategory;
      state.CurrentItem = 0;
    });
  }

  /// <summary>
  /// Marks the entire migration as complete.
  /// </summary>
  public void MarkComplete() {
    UpdateState(state => {
      state.Status = MigrationStatus.Completed;
      state.CompletedAt = DateTimeOffset.UtcNow;
    });
  }

  /// <summary>
  /// Sets a handler decision for a specific file.
  /// </summary>
  public void SetHandlerDecision(string filePath, DecisionChoice choice) {
    Decisions.Handlers.Overrides[filePath] = choice;
  }

  /// <summary>
  /// Gets the handler decision for a file (override or default).
  /// </summary>
  public DecisionChoice GetHandlerDecision(string filePath) {
    return Decisions.Handlers.Overrides.TryGetValue(filePath, out var choice)
        ? choice
        : Decisions.Handlers.Default;
  }

  /// <summary>
  /// Sets a projection decision for a specific file.
  /// </summary>
  public void SetProjectionDecision(string filePath, DecisionChoice choice) {
    Decisions.Projections.Overrides[filePath] = choice;
  }

  /// <summary>
  /// Gets the projection decision for a file (override or default).
  /// </summary>
  public DecisionChoice GetProjectionDecision(string filePath) {
    return Decisions.Projections.Overrides.TryGetValue(filePath, out var choice)
        ? choice
        : Decisions.Projections.Default;
  }
}

/// <summary>
/// Tracks the current state of an in-progress migration.
/// </summary>
public sealed class MigrationState {
  /// <summary>
  /// Current status of the migration.
  /// </summary>
  public MigrationStatus Status { get; set; } = MigrationStatus.NotStarted;

  /// <summary>
  /// When the migration was started.
  /// </summary>
  public DateTimeOffset? StartedAt { get; set; }

  /// <summary>
  /// When the migration state was last updated.
  /// </summary>
  public DateTimeOffset? LastUpdatedAt { get; set; }

  /// <summary>
  /// When the migration was completed.
  /// </summary>
  public DateTimeOffset? CompletedAt { get; set; }

  /// <summary>
  /// Git commit hash before migration started (for revert).
  /// </summary>
  public string? GitCommitBefore { get; set; }

  /// <summary>
  /// Categories that have been fully processed.
  /// </summary>
  public List<string> CompletedCategories { get; set; } = [];

  /// <summary>
  /// Category currently being processed.
  /// </summary>
  public string? CurrentCategory { get; set; }

  /// <summary>
  /// Index of the current item being processed in the current category.
  /// </summary>
  public int CurrentItem { get; set; }
}

/// <summary>
/// Status of a migration operation.
/// </summary>
public enum MigrationStatus {
  /// <summary>
  /// Migration has not been started.
  /// </summary>
  NotStarted,

  /// <summary>
  /// Migration is in progress.
  /// </summary>
  InProgress,

  /// <summary>
  /// Migration has been completed.
  /// </summary>
  Completed,

  /// <summary>
  /// Migration was reverted.
  /// </summary>
  Reverted
}

/// <summary>
/// All migration decisions organized by category.
/// </summary>
public sealed class MigrationDecisions {
  /// <summary>
  /// Handler migration decisions.
  /// </summary>
  public CategoryDecisions Handlers { get; set; } = new();

  /// <summary>
  /// Projection migration decisions.
  /// </summary>
  public ProjectionDecisions Projections { get; set; } = new();

  /// <summary>
  /// Event store operation decisions.
  /// </summary>
  public EventStoreDecisions EventStore { get; set; } = new();

  /// <summary>
  /// ID generation decisions.
  /// </summary>
  public IdGenerationDecisions IdGeneration { get; set; } = new();

  /// <summary>
  /// DI registration decisions.
  /// </summary>
  public CategoryDecisions DiRegistration { get; set; } = new();
}

/// <summary>
/// Generic category decisions with default and per-file overrides.
/// </summary>
public class CategoryDecisions {
  /// <summary>
  /// Default decision for this category.
  /// </summary>
  public DecisionChoice Default { get; set; } = DecisionChoice.Convert;

  /// <summary>
  /// Per-file overrides.
  /// </summary>
  public Dictionary<string, DecisionChoice> Overrides { get; set; } = [];
}

/// <summary>
/// Projection-specific decisions.
/// </summary>
public sealed class ProjectionDecisions : CategoryDecisions {
  /// <summary>
  /// Interface to use for single-stream projections.
  /// </summary>
  public string SingleStream { get; set; } = "IPerspectiveFor";

  /// <summary>
  /// Interface to use for multi-stream projections.
  /// </summary>
  public string MultiStream { get; set; } = "IGlobalPerspectiveFor";
}

/// <summary>
/// Event store operation decisions.
/// </summary>
public sealed class EventStoreDecisions {
  /// <summary>
  /// Decision for AppendExclusive operations.
  /// </summary>
  public DecisionChoice AppendExclusive { get; set; } = DecisionChoice.ConvertWithWarning;

  /// <summary>
  /// Decision for StartStream operations.
  /// </summary>
  public DecisionChoice StartStream { get; set; } = DecisionChoice.Convert;

  /// <summary>
  /// Decision for SaveChangesAsync operations.
  /// </summary>
  public DecisionChoice SaveChanges { get; set; } = DecisionChoice.Skip;
}

/// <summary>
/// ID generation decisions.
/// </summary>
public sealed class IdGenerationDecisions {
  /// <summary>
  /// Decision for Guid.NewGuid() calls.
  /// </summary>
  public DecisionChoice GuidNewGuid { get; set; } = DecisionChoice.Prompt;

  /// <summary>
  /// Decision for CombGuidIdGeneration.NewGuid() calls.
  /// </summary>
  public DecisionChoice CombGuid { get; set; } = DecisionChoice.Convert;
}

/// <summary>
/// A migration decision choice.
/// </summary>
public enum DecisionChoice {
  /// <summary>
  /// Convert to Whizbang equivalent.
  /// </summary>
  Convert,

  /// <summary>
  /// Skip this item (leave unchanged).
  /// </summary>
  Skip,

  /// <summary>
  /// Convert but add a warning/TODO comment.
  /// </summary>
  ConvertWithWarning,

  /// <summary>
  /// Prompt the user for a decision.
  /// </summary>
  Prompt,

  /// <summary>
  /// Apply this decision to all similar items.
  /// </summary>
  ApplyToAll
}
