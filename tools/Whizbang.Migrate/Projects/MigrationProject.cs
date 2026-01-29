using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Migrate.Projects;

/// <summary>
/// JSON serialization context for AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MigrationProject))]
[JsonSerializable(typeof(MigrationProgress))]
[JsonSerializable(typeof(ProjectIndex))]
internal sealed partial class MigrationProjectJsonContext : JsonSerializerContext { }

/// <summary>
/// Represents a migration project that tracks migration state and decisions.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard#project-management</docs>
public sealed record MigrationProject {
  /// <summary>
  /// Name of the migration project (e.g., "orders-migration").
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// Path to the repository being migrated.
  /// </summary>
  public required string RepoPath { get; init; }

  /// <summary>
  /// When this migration project was created.
  /// </summary>
  public required DateTimeOffset CreatedAt { get; init; }

  /// <summary>
  /// Path to the decisions.json file for this project.
  /// </summary>
  public required string DecisionsPath { get; init; }

  /// <summary>
  /// Optional path to a git worktree for isolated migration.
  /// </summary>
  public string? WorktreePath { get; init; }

  /// <summary>
  /// Optional git branch name for worktree.
  /// </summary>
  public string? WorktreeBranch { get; init; }

  /// <summary>
  /// Current progress of the migration.
  /// </summary>
  public MigrationProgress Progress { get; init; } = new();

  /// <summary>
  /// Serializes the project to JSON.
  /// </summary>
  public string ToJson() {
    return JsonSerializer.Serialize(this, MigrationProjectJsonContext.Default.MigrationProject);
  }

  /// <summary>
  /// Deserializes a project from JSON.
  /// </summary>
  public static MigrationProject FromJson(string json) {
    return JsonSerializer.Deserialize(json, MigrationProjectJsonContext.Default.MigrationProject)
        ?? throw new InvalidOperationException("Failed to deserialize migration project");
  }
}

/// <summary>
/// Tracks the progress of a migration project.
/// </summary>
public sealed record MigrationProgress {
  /// <summary>
  /// Number of completed migration steps.
  /// </summary>
  public int CompletedSteps { get; init; }

  /// <summary>
  /// Total number of migration steps.
  /// </summary>
  public int TotalSteps { get; init; }

  /// <summary>
  /// Description of the current step being processed.
  /// </summary>
  public string? CurrentStep { get; init; }

  /// <summary>
  /// When the project was last worked on.
  /// </summary>
  public DateTimeOffset? LastActivity { get; init; }
}

/// <summary>
/// Index file that tracks all projects and the active one.
/// </summary>
internal sealed class ProjectIndex {
  /// <summary>
  /// Name of the currently active project.
  /// </summary>
  public string? ActiveProject { get; set; }

  /// <summary>
  /// List of all project names.
  /// </summary>
  public List<string> Projects { get; set; } = [];
}
