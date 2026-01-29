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
    var projectPath = "/src/MyProject";

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
    var json = """
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
    var json = """
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
    var projectName = "MyProject";

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
}
