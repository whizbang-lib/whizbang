using Whizbang.Migrate.Journal;

namespace Whizbang.Migrate.Tests.Journal;

/// <summary>
/// Coverage-round tests for <see cref="JsonMigrationJournal"/> targeting the branch in
/// SaveAsync that creates the journal's containing directory when it does not yet exist. The
/// primary test suite always pre-creates its temp directory in SetUp, so this path is never
/// exercised there.
/// </summary>
/// <tests>Whizbang.Migrate/Journal/JsonMigrationJournal.cs:52,53</tests>
public class JsonMigrationJournalCoverageTests {
  private string _rootDirectory = null!;

  [Before(Test)]
  public void SetUp() {
    _rootDirectory = Path.Combine(Path.GetTempPath(), $"whizbang-journal-coverage-{Guid.NewGuid():N}");
    // Deliberately not created here -- SaveAsync itself must create it (and any nested
    // subdirectory), which is exactly the branch under test.
  }

  [After(Test)]
  public void TearDown() {
    if (Directory.Exists(_rootDirectory)) {
      Directory.Delete(_rootDirectory, recursive: true);
    }
  }

  // A migration that resumes against a fresh worktree or a brand-new clone has no journal
  // directory yet. If SaveAsync stopped creating it, the very first checkpoint write would
  // throw a DirectoryNotFoundException and abort the migration before anything was recorded --
  // leaving nothing on disk to resume from on retry.
  [Test]
  public async Task SaveAsync_ContainingDirectoryDoesNotExist_CreatesItAsync() {
    // Arrange
    var nestedDirectory = Path.Combine(_rootDirectory, "nested", "journal-dir");
    var journalPath = Path.Combine(nestedDirectory, ".whizbang-migrate.journal.json");
    var journal = new JsonMigrationJournal(journalPath);

    // Act
    await journal.SaveAsync();

    // Assert
    await Assert.That(Directory.Exists(nestedDirectory)).IsTrue()
      .Because("SaveAsync must create its containing directory tree, not assume it already exists");
    await Assert.That(File.Exists(journalPath)).IsTrue();
  }
}
