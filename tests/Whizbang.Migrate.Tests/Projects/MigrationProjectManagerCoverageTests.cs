using System.Text.Json;
using Whizbang.Migrate.Projects;

namespace Whizbang.Migrate.Tests.Projects;

/// <summary>
/// Coverage tests for <see cref="MigrationProjectManager"/> paths the primary test suite doesn't
/// reach: the default base path resolver, the "not found" returns from delete and active-project
/// lookups, the update-progress no-op for an unknown project, and clearing the active-project
/// pointer in the on-disk index when the active project is deleted.
/// </summary>
/// <tests>Whizbang.Migrate/Projects/MigrationProjectManager.cs:*</tests>
public class MigrationProjectManagerCoverageTests {

  // If GetDefaultBasePath ever drifted from ~/.whizbang/migrations, every project created without
  // an explicit base path (the CLI's normal usage) would silently start reading and writing a
  // different directory -- a user would see "no projects found" despite having run migrations for
  // weeks, because every other project already lives at the old path.
  [Test]
  public async Task GetDefaultBasePath_ReturnsWhizbangMigrationsUnderUserProfileAsync() {
    var expected = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".whizbang",
        "migrations");

    var result = MigrationProjectManager.GetDefaultBasePath();

    await Assert.That(result).IsEqualTo(expected)
      .Because("callers that omit a base path rely on this exact location to find prior projects");
  }

  // If deleting a project that was never created reported success, a caller retrying a failed
  // delete -- or a script cleaning up by name -- would believe the project is gone and move on,
  // masking a typo in the project name instead of surfacing it as "not found".
  [Test]
  public async Task DeleteProjectAsync_NonexistentProject_ReturnsFalseAsync() {
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-coverage-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    try {
      var manager = new MigrationProjectManager(tempDir);

      var deleted = await manager.DeleteProjectAsync("never-created");

      await Assert.That(deleted).IsFalse()
        .Because("no directory exists for this project, so nothing was actually removed");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  // If this returned a stale or arbitrary project instead of null when no active project has ever
  // been set for a repository, a wizard resuming an in-progress migration could silently attach
  // itself to the wrong project's decisions file and progress.
  [Test]
  public async Task GetActiveProjectAsync_NoActiveProjectSet_ReturnsNullAsync() {
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-coverage-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    try {
      var manager = new MigrationProjectManager(tempDir);
      await manager.CreateProjectAsync("project-a", "/path/to/repo");

      var active = await manager.GetActiveProjectAsync("/path/to/repo");

      await Assert.That(active).IsNull()
        .Because("a project existing is not the same as one having been marked active");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  // If updating progress for an unknown project name fabricated a project record on disk instead
  // of quietly no-op'ing, a typo in a resumed migration's project name would leave behind a
  // phantom, incomplete project that later shows up in a project listing and confuses whoever is
  // running the migration about which one is real.
  [Test]
  public async Task UpdateProgressAsync_NonexistentProject_DoesNotCreateProjectFileAsync() {
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-coverage-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    try {
      var manager = new MigrationProjectManager(tempDir);

      await manager.UpdateProgressAsync("never-created", 1, 1, "some step");

      var projectDir = Path.Combine(tempDir, "never-created");
      await Assert.That(Directory.Exists(projectDir)).IsFalse()
        .Because("no project directory should be fabricated for an unknown project name");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  // If deleting the currently-active project left the index's active-project pointer dangling, a
  // later GetActiveProjectAsync call could -- if a new project were ever created under the same
  // name -- silently resume against a completely unrelated migration's decisions and progress.
  [Test]
  public async Task DeleteProjectAsync_DeletingTheActiveProject_ClearsItInTheIndexAsync() {
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-coverage-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    try {
      var manager = new MigrationProjectManager(tempDir);
      await manager.CreateProjectAsync("project-a", "/path/to/repo");
      await manager.SetActiveProjectAsync("project-a");

      var indexPath = Path.Combine(tempDir, "index.json");
      using (var beforeDelete = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath))) {
        await Assert.That(beforeDelete.RootElement.GetProperty("active_project").GetString())
          .IsEqualTo("project-a")
          .Because("sanity check that the active project was actually recorded before deletion");
      }

      await manager.DeleteProjectAsync("project-a");

      using var afterDelete = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
      await Assert.That(afterDelete.RootElement.GetProperty("active_project").ValueKind)
        .IsEqualTo(JsonValueKind.Null)
        .Because("deleting the active project must clear the pointer rather than leave it dangling");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }
}
