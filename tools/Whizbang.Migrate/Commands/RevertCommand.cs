using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Commands;

/// <summary>
/// Command that reverts migration changes using git reset.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class RevertCommand {
  /// <summary>
  /// Executes the revert command on the specified directory.
  /// </summary>
  /// <param name="projectPath">Path to the project directory.</param>
  /// <param name="decisionFilePath">Optional custom decision file path.</param>
  /// <param name="deleteDecisionFile">Whether to delete the decision file after revert.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The revert result.</returns>
  public static async Task<RevertResult> ExecuteAsync(
      string projectPath,
      string? decisionFilePath = null,
      bool deleteDecisionFile = false,
      CancellationToken ct = default) {
    // Validate project path
    if (!Directory.Exists(projectPath)) {
      return new RevertResult(false, ErrorMessage: $"Directory not found: {projectPath}");
    }

    // Check for decision file
    var state = await MigrationStateDetector.DetectStateAsync(projectPath, decisionFilePath, ct);

    if (state.DecisionFilePath is null) {
      return new RevertResult(false, ErrorMessage: "No migration found. No decision file exists for this project.");
    }

    // Load the decision file
    DecisionFile decisionFile;
    try {
      decisionFile = await DecisionFile.LoadAsync(state.DecisionFilePath, ct);
    } catch (Exception ex) {
      return new RevertResult(false, ErrorMessage: $"Failed to load decision file: {ex.Message}");
    }

    // Check if migration is already completed
    if (decisionFile.State.Status == MigrationStatus.Completed) {
      return new RevertResult(
          false,
          WarningMessage: "Migration has already been completed. Reverting a completed migration may require manual intervention.");
    }

    // Check if migration is already reverted
    if (decisionFile.State.Status == MigrationStatus.Reverted) {
      return new RevertResult(
          false,
          WarningMessage: "Migration has already been reverted.");
    }

    // Check for git commit to revert to
    if (string.IsNullOrEmpty(decisionFile.State.GitCommitBefore)) {
      return new RevertResult(
          false,
          ErrorMessage: "No git commit recorded before migration. Cannot revert automatically.");
    }

    // Check if this is a git repository
    var isGitRepo = await GitOperations.IsGitRepositoryAsync(projectPath, ct);
    if (!isGitRepo) {
      return new RevertResult(
          false,
          ErrorMessage: "Project is not a git repository. Cannot revert automatically.");
    }

    // Perform git reset
    var resetSuccess = await GitOperations.ResetToCommitAsync(
        projectPath,
        decisionFile.State.GitCommitBefore,
        hard: true,
        ct);

    if (!resetSuccess) {
      return new RevertResult(
          false,
          ErrorMessage: $"Failed to reset to commit {decisionFile.State.GitCommitBefore}");
    }

    // Clean untracked files
    var cleanSuccess = await GitOperations.CleanUntrackedFilesAsync(
        projectPath,
        directories: true,
        force: true,
        ct);

    if (!cleanSuccess) {
      // Non-fatal - reset succeeded but clean failed
      return new RevertResult(
          true,
          WarningMessage: "Reset succeeded but failed to clean untracked files. Some generated files may remain.",
          GitCommitReverted: decisionFile.State.GitCommitBefore);
    }

    // Update decision file status
    decisionFile.UpdateState(s => {
      s.Status = MigrationStatus.Reverted;
    });

    // Save updated decision file or delete
    if (deleteDecisionFile) {
      try {
        File.Delete(state.DecisionFilePath);
      } catch {
        // Non-fatal
      }
    } else {
      try {
        await decisionFile.SaveAsync(state.DecisionFilePath, ct: ct);
      } catch {
        // Non-fatal
      }
    }

    return new RevertResult(
        true,
        GitCommitReverted: decisionFile.State.GitCommitBefore,
        DecisionFileDeleted: deleteDecisionFile);
  }
}

/// <summary>
/// Result of the revert command.
/// </summary>
/// <param name="Success">Whether the revert completed successfully.</param>
/// <param name="GitCommitReverted">The git commit that was reverted to.</param>
/// <param name="DecisionFileDeleted">Whether the decision file was deleted.</param>
/// <param name="WarningMessage">Warning message if any.</param>
/// <param name="ErrorMessage">Error message if the revert failed.</param>
public sealed record RevertResult(
    bool Success,
    string? GitCommitReverted = null,
    bool DecisionFileDeleted = false,
    string? WarningMessage = null,
    string? ErrorMessage = null);
