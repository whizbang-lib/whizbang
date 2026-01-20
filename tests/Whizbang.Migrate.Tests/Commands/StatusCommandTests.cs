using Whizbang.Migrate.Commands;
using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Tests for the status command that reports migration progress.
/// </summary>
/// <tests>Whizbang.Migrate/Commands/StatusCommand.cs:*</tests>
public class StatusCommandTests {
  [Test]
  public async Task ExecuteAsync_NoJournal_ReportsNoMigrationInProgressAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.HasActiveMigration).IsFalse();
      await Assert.That(result.Status).IsEqualTo(JournalStatus.NotStarted);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_WithJournal_ReportsStatusFromJournalAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      // Create a journal file
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, """
        {
          "version": "1.0.0",
          "status": "in_progress",
          "checkpoints": [
            { "id": "chk_001", "commitSha": "abc123", "description": "Initial handler migration" }
          ],
          "transformations": [
            { "transformerName": "HandlerToReceptor", "status": "completed", "filesTransformed": 5 },
            { "transformerName": "ProjectionToPerspective", "status": "pending", "filesTransformed": 0 }
          ]
        }
        """);

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.HasActiveMigration).IsTrue();
      await Assert.That(result.Status).IsEqualTo(JournalStatus.InProgress);
      await Assert.That(result.CheckpointCount).IsEqualTo(1);
      await Assert.That(result.CompletedTransformerCount).IsEqualTo(1);
      await Assert.That(result.PendingTransformerCount).IsEqualTo(1);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_CompletedMigration_ReportsCompletedStatusAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, """
        {
          "version": "1.0.0",
          "status": "completed",
          "checkpoints": [
            { "id": "chk_001", "commitSha": "abc123", "description": "Handler migration" },
            { "id": "chk_002", "commitSha": "def456", "description": "Projection migration" }
          ],
          "transformations": [
            { "transformerName": "HandlerToReceptor", "status": "completed", "filesTransformed": 10 },
            { "transformerName": "ProjectionToPerspective", "status": "completed", "filesTransformed": 5 }
          ]
        }
        """);

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.HasActiveMigration).IsFalse();
      await Assert.That(result.Status).IsEqualTo(JournalStatus.Completed);
      await Assert.That(result.TotalFilesTransformed).IsEqualTo(15);
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_InvalidJournal_ReportsErrorAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, "{ invalid json }");

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsFalse();
      await Assert.That(result.ErrorMessage).Contains("journal");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test]
  public async Task ExecuteAsync_NonExistentDirectory_ReturnsFailureAsync() {
    // Arrange
    var command = new StatusCommand();
    var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

    // Act
    var result = await command.ExecuteAsync(nonExistentPath);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.ErrorMessage).Contains("not found");
  }
}
