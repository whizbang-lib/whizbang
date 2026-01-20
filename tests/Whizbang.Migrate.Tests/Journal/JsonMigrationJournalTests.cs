using System.Globalization;
using Whizbang.Migrate.Core;
using Whizbang.Migrate.Journal;

namespace Whizbang.Migrate.Tests.Journal;

/// <summary>
/// Tests for the JSON-based migration journal that tracks progress and enables
/// idempotent resumption of migrations.
/// </summary>
/// <tests>Whizbang.Migrate/Journal/JsonMigrationJournal.cs:*</tests>
public class JsonMigrationJournalTests {
  private string _tempDirectory = null!;

  [Before(Test)]
  public void SetUp() {
    _tempDirectory = Path.Combine(Path.GetTempPath(), $"whizbang-journal-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDirectory);
  }

  [After(Test)]
  public void TearDown() {
    if (Directory.Exists(_tempDirectory)) {
      Directory.Delete(_tempDirectory, recursive: true);
    }
  }

  [Test]
  public async Task NewJournal_HasNotStartedStatus_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Assert
    await Assert.That(journal.Status).IsEqualTo(JournalStatus.NotStarted);
  }

  [Test]
  public async Task NewJournal_HasEmptyCheckpointsList_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Assert
    await Assert.That(journal.Checkpoints).IsEmpty();
  }

  [Test]
  public async Task NewJournal_HasEmptyTransformationsList_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Assert
    await Assert.That(journal.Transformations).IsEmpty();
  }

  [Test]
  public async Task NewJournal_HasNullWorktree_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Assert
    await Assert.That(journal.Worktree).IsNull();
  }

  [Test]
  public async Task SetWorktree_UpdatesWorktreeProperty_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    var worktree = new WorktreeInfo("/path/to/worktree", "whizbang-migrate/abc123");

    // Act
    journal.SetWorktree(worktree);

    // Assert
    await Assert.That(journal.Worktree).IsNotNull();
    await Assert.That(journal.Worktree!.Path).IsEqualTo("/path/to/worktree");
    await Assert.That(journal.Worktree!.Branch).IsEqualTo("whizbang-migrate/abc123");
  }

  [Test]
  public async Task SetWorktree_ChangesStatusToInProgress_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    var worktree = new WorktreeInfo("/path/to/worktree", "whizbang-migrate/abc123");

    // Act
    journal.SetWorktree(worktree);

    // Assert
    await Assert.That(journal.Status).IsEqualTo(JournalStatus.InProgress);
  }

  [Test]
  public async Task AddCheckpoint_AddsToCheckpointsList_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    var checkpoint = new Checkpoint(
        Id: "chk_001",
        CommitSha: "abc123def456",
        Description: "Initial checkpoint",
        CreatedAt: DateTimeOffset.UtcNow);

    // Act
    journal.AddCheckpoint(checkpoint);

    // Assert
    await Assert.That(journal.Checkpoints.Count).IsEqualTo(1);
    await Assert.That(journal.Checkpoints[0].Id).IsEqualTo("chk_001");
    await Assert.That(journal.Checkpoints[0].CommitSha).IsEqualTo("abc123def456");
  }

  [Test]
  public async Task RecordTransformation_AddsToTransformationsList_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    var transformation = new TransformationRecord(
        TransformerName: "HandlerToReceptor",
        Status: TransformationStatus.Pending,
        Files: new List<string> { "Handler.cs" },
        StartedAt: DateTimeOffset.UtcNow);

    // Act
    journal.RecordTransformation(transformation);

    // Assert
    await Assert.That(journal.Transformations.Count).IsEqualTo(1);
    await Assert.That(journal.Transformations[0].TransformerName).IsEqualTo("HandlerToReceptor");
  }

  [Test]
  public async Task UpdateTransformationStatus_UpdatesExistingTransformation_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    var transformation = new TransformationRecord(
        TransformerName: "HandlerToReceptor",
        Status: TransformationStatus.Pending,
        Files: new List<string> { "Handler.cs" },
        StartedAt: DateTimeOffset.UtcNow);
    journal.RecordTransformation(transformation);

    // Act
    journal.UpdateTransformationStatus("HandlerToReceptor", TransformationStatus.Completed);

    // Assert
    await Assert.That(journal.Transformations[0].Status).IsEqualTo(TransformationStatus.Completed);
  }

  [Test]
  public async Task UpdateTransformationStatus_NonExistent_ThrowsInvalidOperation_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Act & Assert
    await Assert.That(() => journal.UpdateTransformationStatus("NonExistent", TransformationStatus.Completed))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task MarkComplete_ChangesStatusToCompleted_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    journal.SetWorktree(new WorktreeInfo("/path", "branch"));

    // Act
    journal.MarkComplete();

    // Assert
    await Assert.That(journal.Status).IsEqualTo(JournalStatus.Completed);
  }

  [Test]
  public async Task SaveAsync_CreatesJsonFile_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);
    journal.SetWorktree(new WorktreeInfo("/path/to/worktree", "whizbang-migrate/abc123"));
    journal.AddCheckpoint(new Checkpoint("chk_001", "abc123", "Test checkpoint", DateTimeOffset.UtcNow));

    // Act
    await journal.SaveAsync();

    // Assert
    await Assert.That(File.Exists(journalPath)).IsTrue();
    var content = await File.ReadAllTextAsync(journalPath);
    await Assert.That(content).Contains("whizbang-migrate/abc123");
    await Assert.That(content).Contains("chk_001");
  }

  [Test]
  public async Task LoadAsync_RestoresJournalState_Async() {
    // Arrange - Create and save a journal
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var originalJournal = new JsonMigrationJournal(journalPath);
    originalJournal.SetWorktree(new WorktreeInfo("/path/to/worktree", "whizbang-migrate/abc123"));
    originalJournal.AddCheckpoint(new Checkpoint("chk_001", "abc123", "Test checkpoint", DateTimeOffset.UtcNow));
    originalJournal.RecordTransformation(new TransformationRecord(
        "HandlerToReceptor",
        TransformationStatus.Completed,
        new List<string> { "Handler.cs" },
        DateTimeOffset.UtcNow));
    await originalJournal.SaveAsync();

    // Act - Create new journal and load
    var loadedJournal = new JsonMigrationJournal(journalPath);
    await loadedJournal.LoadAsync();

    // Assert
    await Assert.That(loadedJournal.Status).IsEqualTo(JournalStatus.InProgress);
    await Assert.That(loadedJournal.Worktree).IsNotNull();
    await Assert.That(loadedJournal.Worktree!.Path).IsEqualTo("/path/to/worktree");
    await Assert.That(loadedJournal.Checkpoints.Count).IsEqualTo(1);
    await Assert.That(loadedJournal.Transformations.Count).IsEqualTo(1);
  }

  [Test]
  public async Task LoadAsync_NonExistentFile_KeepsDefaultState_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, "nonexistent.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Act
    await journal.LoadAsync();

    // Assert
    await Assert.That(journal.Status).IsEqualTo(JournalStatus.NotStarted);
    await Assert.That(journal.Worktree).IsNull();
    await Assert.That(journal.Checkpoints).IsEmpty();
  }

  [Test]
  public async Task SaveAsync_PreservesVersion_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Act
    await journal.SaveAsync();

    // Assert
    var content = await File.ReadAllTextAsync(journalPath);
    await Assert.That(content).Contains("\"version\"");
    await Assert.That(content).Contains("\"1.0.0\"");
  }

  [Test]
  public async Task MultipleCheckpoints_MaintainsOrder_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Act
    journal.AddCheckpoint(new Checkpoint("chk_001", "sha1", "First", DateTimeOffset.UtcNow));
    journal.AddCheckpoint(new Checkpoint("chk_002", "sha2", "Second", DateTimeOffset.UtcNow));
    journal.AddCheckpoint(new Checkpoint("chk_003", "sha3", "Third", DateTimeOffset.UtcNow));

    // Assert
    await Assert.That(journal.Checkpoints.Count).IsEqualTo(3);
    await Assert.That(journal.Checkpoints[0].Id).IsEqualTo("chk_001");
    await Assert.That(journal.Checkpoints[1].Id).IsEqualTo("chk_002");
    await Assert.That(journal.Checkpoints[2].Id).IsEqualTo("chk_003");
  }

  [Test]
  public async Task RoundTrip_PreservesAllData_Async() {
    // Arrange
    var journalPath = Path.Combine(_tempDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    journal.SetWorktree(new WorktreeInfo("/path/to/worktree", "migrate-branch"));
    journal.AddCheckpoint(new Checkpoint("chk_001", "sha1", "Checkpoint 1", DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture)));
    journal.AddCheckpoint(new Checkpoint("chk_002", "sha2", "Checkpoint 2", DateTimeOffset.Parse("2025-01-02T00:00:00Z", CultureInfo.InvariantCulture)));
    journal.RecordTransformation(new TransformationRecord(
        "Transformer1",
        TransformationStatus.Completed,
        new List<string> { "file1.cs", "file2.cs" },
        DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2025-01-01T01:00:00Z", CultureInfo.InvariantCulture)));

    // Act
    await journal.SaveAsync();

    var loadedJournal = new JsonMigrationJournal(journalPath);
    await loadedJournal.LoadAsync();

    // Assert
    await Assert.That(loadedJournal.Status).IsEqualTo(JournalStatus.InProgress);
    await Assert.That(loadedJournal.Worktree!.Path).IsEqualTo("/path/to/worktree");
    await Assert.That(loadedJournal.Worktree!.Branch).IsEqualTo("migrate-branch");
    await Assert.That(loadedJournal.Checkpoints.Count).IsEqualTo(2);
    await Assert.That(loadedJournal.Checkpoints[0].Description).IsEqualTo("Checkpoint 1");
    await Assert.That(loadedJournal.Transformations.Count).IsEqualTo(1);
    await Assert.That(loadedJournal.Transformations[0].Files.Count).IsEqualTo(2);
  }
}
