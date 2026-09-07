using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage for <see cref="ConsoleRenderer"/> branches not exercised by
/// <see cref="ConsoleRendererTests"/>: <see cref="ConsoleRenderer.RenderError"/>, the
/// started-timestamp line of the in-progress menu, and the previous-completion notice of the
/// fresh-start menu. Every method under test accepts an injected <see cref="TextWriter"/>, so
/// no <c>Console.SetOut</c> or process-global state is needed to observe the output.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/ConsoleRenderer.cs:143,144,145,146,161,162,189,190,191</tests>
public class ConsoleRendererCoverageTests {
  [Test]
  public async Task RenderError_WritesErrorPrefixAndMessage_Async() {
    // If this printed nothing (or dropped the message), a fatal condition during migration
    // would look like the tool silently did nothing, instead of telling the developer what
    // went wrong.
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderError("Failed to write output file", writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Error: Failed to write output file")
      .Because("a developer needs both the fact that this is an error and what specifically failed");
  }

  [Test]
  public async Task RenderMainMenu_InProgress_ShowsStartedTimestamp_WhenStartedAtIsSet_Async() {
    // Without this line, a developer resuming a long-running migration has no way to tell
    // from the menu how long it has been in progress -- e.g. whether it is stale from weeks
    // ago and worth reverting, or from minutes ago and safe to continue.
    var state = new DetectedMigrationState {
      HasMigrationInProgress = true,
      ProjectPath = "/src/MyProject",
      Status = MigrationStatus.InProgress,
      StartedAt = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.Zero)
    };
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderMainMenu(state, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Started:")
      .Because("the label must be present, not just some timestamp digits that happen to appear elsewhere");
  }

  [Test]
  public async Task RenderMainMenu_NotStarted_ShowsPreviousCompletionNotice_WhenLastMigrationCompleted_Async() {
    // Without this notice, a developer starting the wizard fresh after a prior migration
    // already completed would see the identical menu as someone who has never migrated at
    // all, with no hint that re-running "Start new migration" would be redoing finished work.
    var state = new DetectedMigrationState {
      HasMigrationInProgress = false,
      ProjectPath = "/src/MyProject",
      Status = MigrationStatus.Completed
    };
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderMainMenu(state, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Previous migration completed successfully.")
      .Because("this is the only signal in the fresh-start menu that a prior migration exists");
  }
}
