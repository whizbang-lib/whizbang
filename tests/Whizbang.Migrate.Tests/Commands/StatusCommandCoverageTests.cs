using Whizbang.Migrate.Commands;
using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Coverage-round tests for StatusCommand branches not exercised by StatusCommandTests: a
/// journal file that parses as valid JSON but deserializes to no data at all, and the
/// "not_started"/"failed"/unrecognized status strings the in-progress and completed tests
/// don't touch.
/// </summary>
/// <tests>Whizbang.Migrate/Commands/StatusCommand.cs:*</tests>
public class StatusCommandCoverageTests {

  // "{ invalid json }" throws and is reported as a parse failure elsewhere, but the literal
  // JSON value "null" parses cleanly and deserializes to no JournalData at all. If that case
  // fell through to reading journalData.Status, it would throw a NullReferenceException instead
  // of the clear "failed to parse" error an operator can act on.
  [Test]
  public async Task ExecuteAsync_JournalDeserializesToNull_ReportsParseFailureAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, "null");

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsFalse()
        .Because("a journal file that deserializes to nothing carries no status to report");
      await Assert.That(result.ErrorMessage).Contains("Failed to parse")
        .Because("the operator needs to know the journal produced no data, not see a crash");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  // Distinct from the implicit NotStarted reported when no journal file exists at all: this is
  // an explicit "not_started" status written back by the journal itself. If that string fell
  // into the default arm instead of its own case, a legitimate status value would happen to
  // look right today but only by accident of both mapping to the same enum member.
  [Test]
  public async Task ExecuteAsync_NotStartedStatus_ReportsNotStartedAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, """
        {
          "version": "1.0.0",
          "status": "not_started"
        }
        """);

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.Status).IsEqualTo(JournalStatus.NotStarted)
        .Because("the journal's own \"not_started\" string must map to the NotStarted status");
      await Assert.That(result.HasActiveMigration).IsFalse();
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  // A migration that failed partway through must be reported as failed, not silently folded
  // into "not started" -- an operator deciding whether it's safe to re-run needs to know a
  // prior attempt broke, rather than believe nothing had happened yet.
  [Test]
  public async Task ExecuteAsync_FailedStatus_ReportsFailedStatusAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, """
        {
          "version": "1.0.0",
          "status": "failed"
        }
        """);

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.Status).IsEqualTo(JournalStatus.Failed)
        .Because("a journal recorded as \"failed\" must surface as the Failed status");
      await Assert.That(result.HasActiveMigration).IsFalse()
        .Because("a failed migration is not an active one -- re-running is a fresh decision");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  // A status string the tool doesn't recognize (a future value written by a newer tool
  // version, or hand-edited by a developer) must degrade to a safe default rather than throw
  // or misreport an active migration as something it isn't.
  [Test]
  public async Task ExecuteAsync_UnrecognizedStatus_FallsBackToNotStartedAsync() {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-status-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var journalPath = Path.Combine(tempDir, ".whizbang-migrate.journal.json");
      await File.WriteAllTextAsync(journalPath, """
        {
          "version": "1.0.0",
          "status": "some_future_status"
        }
        """);

      var command = new StatusCommand();

      // Act
      var result = await command.ExecuteAsync(tempDir);

      // Assert
      await Assert.That(result.Success).IsTrue();
      await Assert.That(result.Status).IsEqualTo(JournalStatus.NotStarted)
        .Because("an unrecognized status string must degrade to the safe NotStarted default "
               + "rather than throw or be misreported as some other state");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }
}
