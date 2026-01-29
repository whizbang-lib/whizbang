using Whizbang.Migrate.Projects;

namespace Whizbang.Migrate.Tests.Projects;

/// <summary>
/// Tests for the MigrationProjectManager that handles migration project lifecycle.
/// </summary>
/// <tests>Whizbang.Migrate/Projects/MigrationProjectManager.cs:*</tests>
public class MigrationProjectManagerTests {
  private readonly string _tempDir;

  public MigrationProjectManagerTests() {
    _tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
  }

  [Test]
  public async Task CreateProjectAsync_CreatesNewProject_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);

    // Act
    var project = await manager.CreateProjectAsync("orders-migration", "/path/to/repo");

    // Assert
    await Assert.That(project.Name).IsEqualTo("orders-migration");
    await Assert.That(project.RepoPath).IsEqualTo("/path/to/repo");
    await Assert.That(project.CreatedAt).IsNotEqualTo(default(DateTimeOffset));
  }

  [Test]
  public async Task CreateProjectAsync_CreatesDecisionFile_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);

    // Act
    var project = await manager.CreateProjectAsync("orders-migration", "/path/to/repo");

    // Assert
    await Assert.That(project.DecisionsPath).IsNotNull();
    await Assert.That(File.Exists(project.DecisionsPath)).IsTrue();
  }

  [Test]
  public async Task ListProjectsAsync_ReturnsAllProjects_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    await manager.CreateProjectAsync("project-1", "/path/to/repo1");
    await manager.CreateProjectAsync("project-2", "/path/to/repo2");

    // Act
    var projects = await manager.ListProjectsAsync();

    // Assert
    await Assert.That(projects).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ListProjectsForRepoAsync_FiltersProjects_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    await manager.CreateProjectAsync("project-1", "/path/to/repo-a");
    await manager.CreateProjectAsync("project-2", "/path/to/repo-a");
    await manager.CreateProjectAsync("project-3", "/path/to/repo-b");

    // Act
    var projects = await manager.ListProjectsForRepoAsync("/path/to/repo-a");

    // Assert
    await Assert.That(projects).Count().IsEqualTo(2);
  }

  [Test]
  public async Task GetProjectAsync_ReturnsProject_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    await manager.CreateProjectAsync("test-project", "/path/to/repo");

    // Act
    var project = await manager.GetProjectAsync("test-project");

    // Assert
    await Assert.That(project).IsNotNull();
    await Assert.That(project!.Name).IsEqualTo("test-project");
  }

  [Test]
  public async Task GetProjectAsync_ReturnsNullForNonexistent_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);

    // Act
    var project = await manager.GetProjectAsync("nonexistent");

    // Assert
    await Assert.That(project).IsNull();
  }

  [Test]
  public async Task DeleteProjectAsync_RemovesProject_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    await manager.CreateProjectAsync("to-delete", "/path/to/repo");

    // Act
    var deleted = await manager.DeleteProjectAsync("to-delete");

    // Assert
    await Assert.That(deleted).IsTrue();
    var project = await manager.GetProjectAsync("to-delete");
    await Assert.That(project).IsNull();
  }

  [Test]
  public async Task DeleteProjectAsync_KeepsDecisions_WhenRequested_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    var created = await manager.CreateProjectAsync("to-delete", "/path/to/repo");
    var decisionsPath = created.DecisionsPath;

    // Act
    await manager.DeleteProjectAsync("to-delete", keepDecisions: true);

    // Assert
    await Assert.That(File.Exists(decisionsPath)).IsTrue();
  }

  [Test]
  public async Task SetActiveProjectAsync_SetsActiveProject_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    await manager.CreateProjectAsync("project-1", "/path/to/repo");
    await manager.CreateProjectAsync("project-2", "/path/to/repo");

    // Act
    await manager.SetActiveProjectAsync("project-2");
    var active = await manager.GetActiveProjectAsync("/path/to/repo");

    // Assert
    await Assert.That(active).IsNotNull();
    await Assert.That(active!.Name).IsEqualTo("project-2");
  }

  [Test]
  public async Task UpdateProgressAsync_UpdatesProjectProgress_Async() {
    // Arrange
    var manager = new MigrationProjectManager(_tempDir);
    var project = await manager.CreateProjectAsync("test-project", "/path/to/repo");

    // Act
    await manager.UpdateProgressAsync("test-project", 3, 7, "Processing handlers");
    var updated = await manager.GetProjectAsync("test-project");

    // Assert
    await Assert.That(updated!.Progress.CompletedSteps).IsEqualTo(3);
    await Assert.That(updated.Progress.TotalSteps).IsEqualTo(7);
    await Assert.That(updated.Progress.CurrentStep).IsEqualTo("Processing handlers");
  }

  [After(Test)]
  public void Cleanup() {
    if (Directory.Exists(_tempDir)) {
      Directory.Delete(_tempDir, recursive: true);
    }
  }
}
