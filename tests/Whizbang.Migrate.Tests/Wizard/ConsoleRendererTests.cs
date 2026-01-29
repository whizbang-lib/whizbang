using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the ConsoleRenderer that handles terminal UI rendering.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/ConsoleRenderer.cs:*</tests>
public class ConsoleRendererTests {
  [Test]
  public async Task RenderMainMenu_InProgress_ContainsResumeOption_Async() {
    // Arrange
    var state = new DetectedMigrationState {
      HasMigrationInProgress = true,
      ProjectPath = "/src/MyProject",
      Status = MigrationStatus.InProgress,
      CompletedCategories = ["handlers"],
      CurrentCategory = "projections",
      CurrentItem = 5
    };
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderMainMenu(state, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Migration in progress");
    await Assert.That(output).Contains("Continue migration");
  }

  [Test]
  public async Task RenderMainMenu_NotStarted_ContainsStartOption_Async() {
    // Arrange
    var state = new DetectedMigrationState {
      HasMigrationInProgress = false,
      ProjectPath = "/src/MyProject",
      Status = MigrationStatus.NotStarted
    };
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderMainMenu(state, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Analyze codebase");
    await Assert.That(output).Contains("Start new migration");
  }

  [Test]
  public async Task RenderCategoryMenu_ListsAllCategories_Async() {
    // Arrange
    var batches = new List<CategoryBatch> {
      CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler),
        new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler)
      ]),
      CategoryBatch.Create(MigrationCategory.Projections, [
        new MigrationItem("p1.cs", "Projection1", MigrationItemType.Projection)
      ])
    };
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderCategoryMenu(batches, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Handlers");
    await Assert.That(output).Contains("2");
    await Assert.That(output).Contains("Projections");
    await Assert.That(output).Contains("1");
  }

  [Test]
  public async Task RenderDecisionPoint_ShowsOriginalCode_Async() {
    // Arrange
    var point = DecisionPoint.Create(
        filePath: "src/OrderHandler.cs",
        lineNumber: 15,
        displayName: "OrderHandler",
        category: MigrationCategory.Handlers,
        originalCode: "public class OrderHandler : IHandle<CreateOrder> { }",
        options: [
          new DecisionOption("A", "Convert", "public class OrderReceptor : IReceptor<CreateOrder> { }", true),
          new DecisionOption("B", "Skip", null, false)
        ]);
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderDecisionPoint(point, 3, 45, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("3/45");
    await Assert.That(output).Contains("OrderHandler.cs:15");
    await Assert.That(output).Contains("BEFORE");
    await Assert.That(output).Contains("IHandle<CreateOrder>");
  }

  [Test]
  public async Task RenderDecisionPoint_ShowsOptions_Async() {
    // Arrange
    var point = DecisionPoint.Create(
        filePath: "src/OrderHandler.cs",
        lineNumber: 15,
        displayName: "OrderHandler",
        category: MigrationCategory.Handlers,
        originalCode: "original code",
        options: [
          new DecisionOption("A", "Convert to IReceptor<T>", "converted code", true),
          new DecisionOption("B", "Skip", null, false)
        ]);
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderDecisionPoint(point, 1, 10, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("[A]");
    await Assert.That(output).Contains("Convert to IReceptor<T>");
    await Assert.That(output).Contains("(Recommended)");
    await Assert.That(output).Contains("[B]");
    await Assert.That(output).Contains("Skip");
  }

  [Test]
  public async Task RenderCodeBlock_FormatsCodeWithBorder_Async() {
    // Arrange
    var code = "public class Test { }";
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderCodeBlock(code, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("public class Test { }");
    await Assert.That(output).Contains("─"); // Border character
  }

  [Test]
  public async Task RenderProgressBar_ShowsPercentage_Async() {
    // Arrange
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderProgressBar(50, 100, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("50%");
  }

  [Test]
  public async Task RenderProgressBar_ShowsFilledAndEmptyBlocks_Async() {
    // Arrange
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderProgressBar(25, 100, writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("█"); // Filled
    await Assert.That(output).Contains("░"); // Empty
  }

  [Test]
  public async Task RenderWarning_ShowsWarningIcon_Async() {
    // Arrange
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderWarning("Test warning message", writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Warning");
    await Assert.That(output).Contains("Test warning message");
  }

  [Test]
  public async Task RenderSuccess_ShowsSuccessMessage_Async() {
    // Arrange
    var writer = new StringWriter();

    // Act
    ConsoleRenderer.RenderSuccess("Operation completed", writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Operation completed");
  }
}
