using System.Text.Json;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Projects;

/// <summary>
/// Manages migration projects for a codebase.
/// Handles project creation, listing, switching, and deletion.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard#project-management</docs>
public sealed class MigrationProjectManager {
  private readonly string _basePath;
  private const string INDEX_FILE_NAME = "index.json";

  /// <summary>
  /// Creates a project manager with the specified base path for storing projects.
  /// </summary>
  /// <param name="basePath">Base directory for storing migration projects.
  /// Defaults to ~/.whizbang/migrations/</param>
  public MigrationProjectManager(string? basePath = null) {
    _basePath = basePath ?? GetDefaultBasePath();
    Directory.CreateDirectory(_basePath);
  }

  /// <summary>
  /// Gets the default base path for migration projects.
  /// </summary>
  public static string GetDefaultBasePath() {
    var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(userHome, ".whizbang", "migrations");
  }

  /// <summary>
  /// Creates a new migration project.
  /// </summary>
  /// <param name="name">Name of the project (e.g., "orders-migration").</param>
  /// <param name="repoPath">Path to the repository being migrated.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The created migration project.</returns>
  public async Task<MigrationProject> CreateProjectAsync(
      string name,
      string repoPath,
      CancellationToken ct = default) {
    var projectDir = _getProjectDirectory(name);
    Directory.CreateDirectory(projectDir);

    var decisionsPath = Path.Combine(projectDir, "decisions.json");

    var project = new MigrationProject {
      Name = name,
      RepoPath = repoPath,
      CreatedAt = DateTimeOffset.UtcNow,
      DecisionsPath = decisionsPath,
      Progress = new MigrationProgress()
    };

    // Save the project
    var projectPath = Path.Combine(projectDir, "project.json");
    await File.WriteAllTextAsync(projectPath, project.ToJson(), ct);

    // Create an empty decision file
    var decisionFile = DecisionFile.Create(repoPath);
    await decisionFile.SaveAsync(decisionsPath, ct);

    // Update the index
    await _addToIndexAsync(name, ct);

    return project;
  }

  /// <summary>
  /// Lists all migration projects.
  /// </summary>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>List of all migration projects.</returns>
  public async Task<IReadOnlyList<MigrationProject>> ListProjectsAsync(CancellationToken ct = default) {
    var index = await _loadIndexAsync(ct);
    var projects = new List<MigrationProject>();

    foreach (var projectName in index.Projects) {
      var project = await GetProjectAsync(projectName, ct);
      if (project is not null) {
        projects.Add(project);
      }
    }

    return projects;
  }

  /// <summary>
  /// Lists migration projects for a specific repository.
  /// </summary>
  /// <param name="repoPath">Path to the repository.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>List of migration projects for the repository.</returns>
  public async Task<IReadOnlyList<MigrationProject>> ListProjectsForRepoAsync(
      string repoPath,
      CancellationToken ct = default) {
    var allProjects = await ListProjectsAsync(ct);
    return allProjects.Where(p => p.RepoPath == repoPath).ToList();
  }

  /// <summary>
  /// Gets a migration project by name.
  /// </summary>
  /// <param name="name">Name of the project.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The migration project, or null if not found.</returns>
  public async Task<MigrationProject?> GetProjectAsync(string name, CancellationToken ct = default) {
    var projectPath = Path.Combine(_getProjectDirectory(name), "project.json");
    if (!File.Exists(projectPath)) {
      return null;
    }

    var json = await File.ReadAllTextAsync(projectPath, ct);
    return MigrationProject.FromJson(json);
  }

  /// <summary>
  /// Deletes a migration project.
  /// </summary>
  /// <param name="name">Name of the project to delete.</param>
  /// <param name="keepDecisions">Whether to keep the decisions file.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>True if the project was deleted, false if not found.</returns>
  public async Task<bool> DeleteProjectAsync(
      string name,
      bool keepDecisions = false,
      CancellationToken ct = default) {
    var projectDir = _getProjectDirectory(name);
    if (!Directory.Exists(projectDir)) {
      return false;
    }

    if (keepDecisions) {
      // Delete everything except decisions.json
      foreach (var file in Directory.GetFiles(projectDir)) {
        if (!file.EndsWith("decisions.json", StringComparison.OrdinalIgnoreCase)) {
          File.Delete(file);
        }
      }
    } else {
      Directory.Delete(projectDir, recursive: true);
    }

    // Remove from index
    await _removeFromIndexAsync(name, ct);

    return true;
  }

  /// <summary>
  /// Sets the active project for a repository.
  /// </summary>
  /// <param name="name">Name of the project to make active.</param>
  /// <param name="ct">Cancellation token.</param>
  public async Task SetActiveProjectAsync(string name, CancellationToken ct = default) {
    var index = await _loadIndexAsync(ct);
    index.ActiveProject = name;
    await _saveIndexAsync(index, ct);
  }

  /// <summary>
  /// Gets the active project for a repository.
  /// </summary>
  /// <param name="repoPath">Path to the repository.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The active project, or null if none.</returns>
  public async Task<MigrationProject?> GetActiveProjectAsync(string repoPath, CancellationToken ct = default) {
    var index = await _loadIndexAsync(ct);
    if (string.IsNullOrEmpty(index.ActiveProject)) {
      return null;
    }

    var project = await GetProjectAsync(index.ActiveProject, ct);
    return project?.RepoPath == repoPath ? project : null;
  }

  /// <summary>
  /// Updates the progress of a migration project.
  /// </summary>
  /// <param name="name">Name of the project.</param>
  /// <param name="completedSteps">Number of completed steps.</param>
  /// <param name="totalSteps">Total number of steps.</param>
  /// <param name="currentStep">Description of the current step.</param>
  /// <param name="ct">Cancellation token.</param>
  public async Task UpdateProgressAsync(
      string name,
      int completedSteps,
      int totalSteps,
      string? currentStep = null,
      CancellationToken ct = default) {
    var project = await GetProjectAsync(name, ct);
    if (project is null) {
      return;
    }

    var updated = project with {
      Progress = new MigrationProgress {
        CompletedSteps = completedSteps,
        TotalSteps = totalSteps,
        CurrentStep = currentStep,
        LastActivity = DateTimeOffset.UtcNow
      }
    };

    var projectPath = Path.Combine(_getProjectDirectory(name), "project.json");
    await File.WriteAllTextAsync(projectPath, updated.ToJson(), ct);
  }

  private string _getProjectDirectory(string name) {
    return Path.Combine(_basePath, name);
  }

  private string _getIndexPath() {
    return Path.Combine(_basePath, INDEX_FILE_NAME);
  }

  private async Task<ProjectIndex> _loadIndexAsync(CancellationToken ct) {
    var indexPath = _getIndexPath();
    if (!File.Exists(indexPath)) {
      return new ProjectIndex();
    }

    var json = await File.ReadAllTextAsync(indexPath, ct);
    return JsonSerializer.Deserialize(json, MigrationProjectJsonContext.Default.ProjectIndex)
        ?? new ProjectIndex();
  }

  private async Task _saveIndexAsync(ProjectIndex index, CancellationToken ct) {
    var json = JsonSerializer.Serialize(index, MigrationProjectJsonContext.Default.ProjectIndex);
    await File.WriteAllTextAsync(_getIndexPath(), json, ct);
  }

  private async Task _addToIndexAsync(string name, CancellationToken ct) {
    var index = await _loadIndexAsync(ct);
    if (!index.Projects.Contains(name)) {
      index.Projects.Add(name);
      await _saveIndexAsync(index, ct);
    }
  }

  private async Task _removeFromIndexAsync(string name, CancellationToken ct) {
    var index = await _loadIndexAsync(ct);
    index.Projects.Remove(name);
    if (index.ActiveProject == name) {
      index.ActiveProject = null;
    }
    await _saveIndexAsync(index, ct);
  }
}
