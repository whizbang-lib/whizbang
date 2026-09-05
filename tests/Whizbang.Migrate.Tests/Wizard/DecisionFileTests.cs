using System.Text.Json;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the DecisionFile model that stores migration decisions and state.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/DecisionFile.cs:*</tests>
public class DecisionFileTests {
  [Test]
  public async Task Create_ReturnsNewDecisionFileWithDefaults_Async() {
    // Arrange
    const string projectPath = "/src/MyProject";

    // Act
    var decisionFile = DecisionFile.Create(projectPath);

    // Assert
    await Assert.That(decisionFile.Version).IsEqualTo("1.0");
    await Assert.That(decisionFile.ProjectPath).IsEqualTo(projectPath);
    await Assert.That(decisionFile.State).IsNotNull();
    await Assert.That(decisionFile.State.Status).IsEqualTo(MigrationStatus.NotStarted);
    await Assert.That(decisionFile.Decisions).IsNotNull();
  }

  [Test]
  public async Task SerializeToJson_ProducesValidJson_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.Decisions.Handlers.Default = DecisionChoice.Convert;
    decisionFile.Decisions.Handlers.Overrides["src/Legacy.cs"] = DecisionChoice.Skip;

    // Act
    var json = decisionFile.ToJson();

    // Assert - snake_case naming policy transforms property names
    await Assert.That(json).Contains("\"version\": \"1.0\"");
    await Assert.That(json).Contains("\"project_path\": \"/src/MyProject\"");
    await Assert.That(json).Contains("\"handlers\"");
    await Assert.That(json).Contains("\"default\": \"Convert\"");
  }

  [Test]
  public async Task DeserializeFromJson_RestoresDecisionFile_Async() {
    // Arrange - using snake_case property names to match JSON serialization policy
    const string json = """
      {
        "version": "1.0",
        "project_path": "/src/MyProject",
        "generated_at": "2026-01-20T10:00:00Z",
        "state": {
          "status": "InProgress",
          "started_at": "2026-01-18T14:30:00Z",
          "last_updated_at": "2026-01-20T10:00:00Z",
          "git_commit_before": "abc123",
          "completed_categories": ["handlers"],
          "current_category": "projections",
          "current_item": 5
        },
        "decisions": {
          "handlers": {
            "default": "Convert",
            "overrides": {
              "src/Legacy.cs": "Skip"
            }
          },
          "projections": {
            "default": "Convert",
            "single_stream": "IPerspectiveFor",
            "multi_stream": "IGlobalPerspectiveFor"
          }
        }
      }
      """;

    // Act
    var decisionFile = DecisionFile.FromJson(json);

    // Assert
    await Assert.That(decisionFile.Version).IsEqualTo("1.0");
    await Assert.That(decisionFile.ProjectPath).IsEqualTo("/src/MyProject");
    await Assert.That(decisionFile.State.Status).IsEqualTo(MigrationStatus.InProgress);
    await Assert.That(decisionFile.State.GitCommitBefore).IsEqualTo("abc123");
    await Assert.That(decisionFile.State.CompletedCategories).Contains("handlers");
    await Assert.That(decisionFile.State.CurrentCategory).IsEqualTo("projections");
    await Assert.That(decisionFile.Decisions.Handlers.Default).IsEqualTo(DecisionChoice.Convert);
    await Assert.That(decisionFile.Decisions.Handlers.Overrides["src/Legacy.cs"]).IsEqualTo(DecisionChoice.Skip);
  }

  [Test]
  public async Task SaveToFile_WritesJsonToPath_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-decisions-{Guid.NewGuid()}.json");
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.Decisions.Handlers.Default = DecisionChoice.Convert;

    try {
      // Act
      await decisionFile.SaveAsync(tempPath);

      // Assert
      await Assert.That(File.Exists(tempPath)).IsTrue();
      var content = await File.ReadAllTextAsync(tempPath);
      await Assert.That(content).Contains("\"version\": \"1.0\"");
      await Assert.That(content).Contains("\"project_path\": \"/src/MyProject\"");
    } finally {
      if (File.Exists(tempPath)) {
        File.Delete(tempPath);
      }
    }
  }

  [Test]
  public async Task LoadFromFile_ReadsJsonFromPath_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-decisions-{Guid.NewGuid()}.json");
    const string json = """
      {
        "version": "1.0",
        "project_path": "/src/TestProject",
        "state": { "status": "NotStarted" },
        "decisions": {
          "handlers": { "default": "Skip" }
        }
      }
      """;

    try {
      await File.WriteAllTextAsync(tempPath, json);

      // Act
      var decisionFile = await DecisionFile.LoadAsync(tempPath);

      // Assert
      await Assert.That(decisionFile.ProjectPath).IsEqualTo("/src/TestProject");
      await Assert.That(decisionFile.Decisions.Handlers.Default).IsEqualTo(DecisionChoice.Skip);
    } finally {
      if (File.Exists(tempPath)) {
        File.Delete(tempPath);
      }
    }
  }

  [Test]
  public async Task GetDefaultPath_ReturnsUserProfilePath_Async() {
    // Arrange
    const string projectName = "MyProject";

    // Act
    var defaultPath = DecisionFile.GetDefaultPath(projectName);

    // Assert
    var expectedBase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".whizbang",
        "migrations",
        projectName,
        "decisions.json");
    await Assert.That(defaultPath).IsEqualTo(expectedBase);
  }

  [Test]
  public async Task UpdateState_SetsLastUpdatedAt_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");
    var before = DateTimeOffset.UtcNow;

    // Act
    decisionFile.UpdateState(state => {
      state.Status = MigrationStatus.InProgress;
      state.CurrentCategory = "handlers";
    });

    // Assert
    await Assert.That(decisionFile.State.Status).IsEqualTo(MigrationStatus.InProgress);
    await Assert.That(decisionFile.State.CurrentCategory).IsEqualTo("handlers");
    await Assert.That(decisionFile.State.LastUpdatedAt!.Value).IsGreaterThanOrEqualTo(before);
  }

  [Test]
  public async Task MarkCategoryComplete_AddsToCompletedAndMovesToNext_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.State.CurrentCategory = "handlers";

    // Act
    decisionFile.MarkCategoryComplete("handlers", "projections");

    // Assert
    await Assert.That(decisionFile.State.CompletedCategories).Contains("handlers");
    await Assert.That(decisionFile.State.CurrentCategory).IsEqualTo("projections");
    await Assert.That(decisionFile.State.CurrentItem).IsEqualTo(0);
  }

  [Test]
  public async Task MarkComplete_SetsStatusToCompleted_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.State.Status = MigrationStatus.InProgress;

    // Act
    decisionFile.MarkComplete();

    // Assert
    await Assert.That(decisionFile.State.Status).IsEqualTo(MigrationStatus.Completed);
    await Assert.That(decisionFile.State.CompletedAt).IsNotNull();
  }

  [Test]
  public async Task SetDecision_StoresDecisionForFile_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");

    // Act
    decisionFile.SetHandlerDecision("src/Handlers/OrderHandler.cs", DecisionChoice.Convert);
    decisionFile.SetHandlerDecision("src/Handlers/LegacyHandler.cs", DecisionChoice.Skip);

    // Assert
    await Assert.That(decisionFile.Decisions.Handlers.Overrides["src/Handlers/OrderHandler.cs"])
        .IsEqualTo(DecisionChoice.Convert);
    await Assert.That(decisionFile.Decisions.Handlers.Overrides["src/Handlers/LegacyHandler.cs"])
        .IsEqualTo(DecisionChoice.Skip);
  }

  [Test]
  public async Task GetDecision_ReturnsOverrideIfExists_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.Decisions.Handlers.Default = DecisionChoice.Convert;
    decisionFile.Decisions.Handlers.Overrides["src/Legacy.cs"] = DecisionChoice.Skip;

    // Act
    var defaultDecision = decisionFile.GetHandlerDecision("src/NewHandler.cs");
    var overrideDecision = decisionFile.GetHandlerDecision("src/Legacy.cs");

    // Assert
    await Assert.That(defaultDecision).IsEqualTo(DecisionChoice.Convert);
    await Assert.That(overrideDecision).IsEqualTo(DecisionChoice.Skip);
  }

  [Test]
  public async Task RoundTrip_PreservesAllData_Async() {
    // Arrange
    var original = DecisionFile.Create("/src/MyProject");
    original.State.Status = MigrationStatus.InProgress;
    original.State.GitCommitBefore = "abc123";
    original.State.CompletedCategories.Add("handlers");
    original.State.CurrentCategory = "projections";
    original.State.CurrentItem = 5;
    original.Decisions.Handlers.Default = DecisionChoice.Convert;
    original.Decisions.Handlers.Overrides["legacy.cs"] = DecisionChoice.Skip;
    original.Decisions.Projections.Default = DecisionChoice.Convert;
    original.Decisions.Projections.SingleStream = "IPerspectiveFor";
    original.Decisions.EventStore.AppendExclusive = DecisionChoice.ConvertWithWarning;
    original.Decisions.IdGeneration.GuidNewGuid = DecisionChoice.Prompt;

    // Act
    var json = original.ToJson();
    var restored = DecisionFile.FromJson(json);

    // Assert
    await Assert.That(restored.Version).IsEqualTo(original.Version);
    await Assert.That(restored.ProjectPath).IsEqualTo(original.ProjectPath);
    await Assert.That(restored.State.Status).IsEqualTo(original.State.Status);
    await Assert.That(restored.State.GitCommitBefore).IsEqualTo(original.State.GitCommitBefore);
    await Assert.That(restored.State.CompletedCategories).IsEquivalentTo(original.State.CompletedCategories);
    await Assert.That(restored.State.CurrentCategory).IsEqualTo(original.State.CurrentCategory);
    await Assert.That(restored.State.CurrentItem).IsEqualTo(original.State.CurrentItem);
    await Assert.That(restored.Decisions.Handlers.Default).IsEqualTo(original.Decisions.Handlers.Default);
    await Assert.That(restored.Decisions.Handlers.Overrides["legacy.cs"]).IsEqualTo(DecisionChoice.Skip);
    await Assert.That(restored.Decisions.Projections.SingleStream).IsEqualTo(original.Decisions.Projections.SingleStream);
    await Assert.That(restored.Decisions.EventStore.AppendExclusive).IsEqualTo(original.Decisions.EventStore.AppendExclusive);
    await Assert.That(restored.Decisions.IdGeneration.GuidNewGuid).IsEqualTo(original.Decisions.IdGeneration.GuidNewGuid);
  }

  // ── Commented (JSONC) form ────────────────────────────────────────────────

  private static DecisionFile _fullyDecided() {
    var file = DecisionFile.Create("/src/MyProject");
    file.State.Status = MigrationStatus.InProgress;
    file.State.CompletedCategories.Add("handlers");
    file.State.CurrentCategory = "projections";
    file.State.CurrentItem = 5;
    file.Decisions.Handlers.Default = DecisionChoice.Skip;
    file.Decisions.Handlers.Overrides["legacy.cs"] = DecisionChoice.Convert;
    file.Decisions.Projections.Default = DecisionChoice.ConvertWithWarning;
    file.State.GitCommitBefore = "abc123";
    file.ExcludePatterns.Add("**/Generated/**");
    return file;
  }

  [Test]
  public async Task ToJsonWithComments_RoundTripsBackThroughFromJson_Async() {
    // The commented form is hand-built string by string rather than serialized, so it is a
    // second implementation of the same document that nothing keeps in step with the model.
    // If it ever stops parsing, a user who saved with comments cannot resume at all.
    var original = _fullyDecided();

    var restored = DecisionFile.FromJson(original.ToJsonWithComments());

    await Assert.That(restored.ProjectPath).IsEqualTo(original.ProjectPath);
    await Assert.That(restored.Version).IsEqualTo(original.Version);
  }

  [Test]
  public async Task ToJsonWithComments_PreservesTheDecisionsAUserMadeByHand_Async() {
    // Per-file overrides and category defaults are the whole point of the file. Losing one does
    // not throw -- it silently reverts to the default and re-converts code somebody chose to
    // skip, which is the failure this format exists to prevent.
    var original = _fullyDecided();

    var restored = DecisionFile.FromJson(original.ToJsonWithComments());

    await Assert.That(restored.Decisions.Handlers.Default).IsEqualTo(DecisionChoice.Skip);
    await Assert.That(restored.Decisions.Projections.Default).IsEqualTo(DecisionChoice.ConvertWithWarning);
    await Assert.That(restored.GetHandlerDecision("legacy.cs")).IsEqualTo(DecisionChoice.Convert)
      .Because("a per-file override is a decision somebody made one file at a time; losing it "
             + "silently re-converts code they chose to leave alone");
  }

  [Test]
  public async Task ToJsonWithComments_PreservesResumeState_Async() {
    // Resume reads exactly these fields. Losing them restarts a partially finished migration
    // from the top and re-asks every question the user already answered.
    var original = _fullyDecided();

    var restored = DecisionFile.FromJson(original.ToJsonWithComments());

    await Assert.That(restored.State.Status).IsEqualTo(MigrationStatus.InProgress);
    await Assert.That(restored.State.CompletedCategories).Contains("handlers");
    await Assert.That(restored.State.CurrentCategory).IsEqualTo("projections");
    await Assert.That(restored.State.CurrentItem).IsEqualTo(5);
    await Assert.That(restored.State.GitCommitBefore).IsEqualTo("abc123")
      .Because("the pre-migration commit is what a revert rewinds to -- losing it strands the "
             + "migration with no way back");
    await Assert.That(restored.ExcludePatterns).Contains("**/Generated/**");
  }

  [Test]
  public async Task ToJsonWithComments_ActuallyCarriesComments_Async() {
    // Otherwise this overload is just a slower ToJson. The comments are the reason a user is
    // invited to open and hand-edit the file.
    var json = _fullyDecided().ToJsonWithComments();

    await Assert.That(json).Contains("//");
  }

  [Test]
  public async Task SaveAsync_WithComments_IsReadableByLoadAsync_Async() {
    // End to end on disk: the commented file is the one a user edits, so a save it cannot load
    // back would strand the migration.
    var dir = Path.Combine(Path.GetTempPath(), "whizbang-decisionfile", Guid.NewGuid().ToString("N"));
    try {
      var path = Path.Combine(dir, "decisions.jsonc");
      var original = _fullyDecided();

      await original.SaveAsync(path, includeComments: true);
      var loaded = await DecisionFile.LoadAsync(path);

      await Assert.That(loaded.ProjectPath).IsEqualTo(original.ProjectPath);
      await Assert.That(loaded.State.CurrentCategory).IsEqualTo("projections");
      await Assert.That(await File.ReadAllTextAsync(path)).Contains("//");
    } finally {
      if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
    }
  }

  // ── Paths, defaults and failure modes ─────────────────────────────────────

  [Test]
  public async Task SaveAsync_CreatesMissingDirectories_Async() {
    // The default location sits under the user profile and does not exist on a first run.
    var root = Path.Combine(Path.GetTempPath(), "whizbang-decisionfile", Guid.NewGuid().ToString("N"));
    try {
      var path = Path.Combine(root, "nested", "deeper", "decisions.json");

      await DecisionFile.Create("/src/MyProject").SaveAsync(path);

      await Assert.That(File.Exists(path)).IsTrue();
    } finally {
      if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
    }
  }

  [Test]
  public async Task GetProjectionDecision_WithoutAnOverride_UsesTheCategoryDefault_Async() {
    // Overrides are sparse by design, so the default carries most files. A fallback that
    // ignored it would apply the wrong action to everything not named explicitly.
    var file = DecisionFile.Create("/src/MyProject");
    file.Decisions.Projections.Default = DecisionChoice.Prompt;

    await Assert.That(file.GetProjectionDecision("src/View.cs")).IsEqualTo(DecisionChoice.Prompt);

    file.SetProjectionDecision("src/View.cs", DecisionChoice.Skip);

    await Assert.That(file.GetProjectionDecision("src/View.cs")).IsEqualTo(DecisionChoice.Skip);
    await Assert.That(file.GetProjectionDecision("src/Other.cs")).IsEqualTo(DecisionChoice.Prompt)
      .Because("one override must not move the default for every other file");
  }

  [Test]
  public async Task MarkCategoryComplete_RepeatedForTheSameCategory_DoesNotDuplicateIt_Async() {
    // Resuming replays the same transition, so this has to be idempotent or the completed list
    // grows on every restart.
    var file = DecisionFile.Create("/src/MyProject");

    file.MarkCategoryComplete("handlers", "projections");
    file.MarkCategoryComplete("handlers", "projections");

    await Assert.That(file.State.CompletedCategories.Count(c => c == "handlers")).IsEqualTo(1);
  }

  [Test]
  public async Task MarkCategoryComplete_ResetsThePositionWithinTheCategory_Async() {
    // A stale CurrentItem would resume the next category partway through and skip its first
    // entries without reporting anything.
    var file = DecisionFile.Create("/src/MyProject");
    file.UpdateState(s => s.CurrentItem = 7);

    file.MarkCategoryComplete("handlers", "projections");

    await Assert.That(file.State.CurrentItem).IsEqualTo(0);
  }

  [Test]
  public async Task MarkCategoryComplete_WithNoNextCategory_ClearsTheCurrentOne_Async() {
    // The end of the run: leaving the last category set would make a finished migration look
    // like it still had work pending.
    var file = DecisionFile.Create("/src/MyProject");
    file.MarkCategoryComplete("handlers", "projections");

    file.MarkCategoryComplete("projections", null);

    await Assert.That(file.State.CurrentCategory).IsNull();
  }

  [Test]
  public async Task FromJson_OnJsonNull_ThrowsRatherThanReturningABlankFile_Async() {
    // Handing back an empty decision file would read as "nothing decided yet" and quietly
    // re-ask or re-convert everything the user already answered.
    await Assert.That(() => DecisionFile.FromJson("null")).Throws<InvalidOperationException>();
  }


}
