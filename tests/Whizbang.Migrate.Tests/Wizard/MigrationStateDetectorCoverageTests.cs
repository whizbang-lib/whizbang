using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage for <see cref="MigrationStateDetector"/> branches not exercised by
/// <see cref="MigrationStateDetectorTests"/>: the decision-file-exists-but-unreadable path,
/// and the three <see cref="MigrationStateDetector.GetProgressSummary"/> status branches
/// other than in-progress.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/MigrationStateDetector.cs:53,55,56,57,58,59,60,61,72,76,80,152</tests>
public class MigrationStateDetectorCoverageTests {
  [Test]
  public async Task DetectStateAsync_ReportsUnreadableFile_WhenDecisionFileIsCorrupt_Async() {
    // If a corrupt decision file were silently treated the same as "no migration ever
    // started", a developer who already made real decisions could be walked through the
    // wizard from scratch and have their prior choices clobbered on save. The detector must
    // surface that the file exists but could not be trusted, via a distinct Error message.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var decisionFilePath = Path.Combine(Path.GetTempPath(), $"corrupt-decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      await File.WriteAllTextAsync(decisionFilePath, "{ this is not valid json");

      // Act
      var state = await MigrationStateDetector.DetectStateAsync(tempPath, decisionFilePath);

      // Assert
      await Assert.That(state.HasMigrationInProgress).IsFalse()
        .Because("a decision file we can't parse must never be treated as an active migration");
      await Assert.That(state.DecisionFilePath).IsEqualTo(decisionFilePath)
        .Because("the path is still known even though the content couldn't be read");
      await Assert.That(state.Status).IsEqualTo(MigrationStatus.NotStarted);
      await Assert.That(state.ProjectPath).IsEqualTo(tempPath);
      await Assert.That(state.Error).IsEqualTo("Could not read decision file")
        .Because("this is what distinguishes a genuinely fresh project from one whose decision file is broken");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(decisionFilePath)) {
        File.Delete(decisionFilePath);
      }
    }
  }

  [Test]
  public async Task GetProgressSummary_ReturnsNotStartedMessage_WhenStatusIsNotStarted_Async() {
    // If this branch silently fell through to the in-progress formatting below it, a fresh
    // project would show a bogus "Completed: [none] | Current: none (item 0)" line instead
    // of telling the developer nothing has happened yet.
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.State.Status = MigrationStatus.NotStarted;

    // Act
    var summary = MigrationStateDetector.GetProgressSummary(decisionFile);

    // Assert
    await Assert.That(summary).IsEqualTo("Migration not started");
  }

  [Test]
  public async Task GetProgressSummary_ReturnsCompletionNotice_WhenStatusIsCompleted_Async() {
    // Without this branch a finished migration would fall through to the in-progress
    // formatting, telling a developer decisions are still pending when the work is done.
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.State.Status = MigrationStatus.Completed;
    decisionFile.State.CompletedAt = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    // Act
    var summary = MigrationStateDetector.GetProgressSummary(decisionFile);

    // Assert
    await Assert.That(summary).Contains("Migration completed at")
      .Because("a developer re-running the wizard needs to know this project is already done, not mid-flight");
  }

  [Test]
  public async Task GetProgressSummary_ReturnsRevertedMessage_WhenStatusIsReverted_Async() {
    // Without this branch a reverted migration would fall through to the in-progress
    // formatting, implying there is still work to resume when the changes were rolled back.
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.State.Status = MigrationStatus.Reverted;

    // Act
    var summary = MigrationStateDetector.GetProgressSummary(decisionFile);

    // Assert
    await Assert.That(summary).IsEqualTo("Migration was reverted");
  }
}
