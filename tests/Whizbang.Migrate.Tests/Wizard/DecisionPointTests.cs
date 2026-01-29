using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the DecisionPoint model that represents a single migration decision.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/DecisionPoint.cs:*</tests>
public class DecisionPointTests {
  [Test]
  public async Task Create_CreatesDecisionPoint_WithAllProperties_Async() {
    // Arrange
    var originalCode = "public class OrderHandler : IHandle<CreateOrder> { }";
    var options = new List<DecisionOption> {
      new("A", "Convert to IReceptor<T>", "public class OrderReceptor : IReceptor<CreateOrder> { }", true),
      new("B", "Skip", null, false)
    };

    // Act
    var point = DecisionPoint.Create(
        filePath: "src/Handlers/OrderHandler.cs",
        lineNumber: 15,
        displayName: "OrderHandler",
        category: MigrationCategory.Handlers,
        originalCode: originalCode,
        options: options);

    // Assert
    await Assert.That(point.FilePath).IsEqualTo("src/Handlers/OrderHandler.cs");
    await Assert.That(point.LineNumber).IsEqualTo(15);
    await Assert.That(point.DisplayName).IsEqualTo("OrderHandler");
    await Assert.That(point.Category).IsEqualTo(MigrationCategory.Handlers);
    await Assert.That(point.OriginalCode).IsEqualTo(originalCode);
    await Assert.That(point.Options.Count).IsEqualTo(2);
  }

  [Test]
  public async Task DecisionOption_RecommendedOption_HasIsRecommendedTrue_Async() {
    // Arrange & Act
    var option = new DecisionOption("A", "Convert", "converted code", true);

    // Assert
    await Assert.That(option.Key).IsEqualTo("A");
    await Assert.That(option.Label).IsEqualTo("Convert");
    await Assert.That(option.TransformedCode).IsEqualTo("converted code");
    await Assert.That(option.IsRecommended).IsTrue();
  }

  [Test]
  public async Task DecisionOption_SkipOption_HasNullTransformedCode_Async() {
    // Arrange & Act
    var option = new DecisionOption("B", "Skip", null, false);

    // Assert
    await Assert.That(option.TransformedCode).IsNull();
    await Assert.That(option.IsRecommended).IsFalse();
  }

  [Test]
  public async Task SelectOption_SetsSelectedOption_Async() {
    // Arrange
    var options = new List<DecisionOption> {
      new("A", "Convert", "code", true),
      new("B", "Skip", null, false)
    };
    var point = DecisionPoint.Create("file.cs", 1, "Test", MigrationCategory.Handlers, "original", options);

    // Act
    point.SelectOption("A");

    // Assert
    await Assert.That(point.SelectedOption).IsEqualTo("A");
    await Assert.That(point.IsDecided).IsTrue();
  }

  [Test]
  public async Task SelectOption_WithApplyToAll_SetsApplyToAllFlag_Async() {
    // Arrange
    var options = new List<DecisionOption> {
      new("A", "Convert", "code", true),
      new("C", "Apply to all similar", null, false)
    };
    var point = DecisionPoint.Create("file.cs", 1, "Test", MigrationCategory.Handlers, "original", options);

    // Act
    point.SelectOption("A", applyToAll: true);

    // Assert
    await Assert.That(point.SelectedOption).IsEqualTo("A");
    await Assert.That(point.ApplyToAll).IsTrue();
  }

  [Test]
  public async Task GetSelectedTransformedCode_ReturnsCorrectCode_WhenOptionSelected_Async() {
    // Arrange
    var transformedCode = "public class OrderReceptor : IReceptor<CreateOrder> { }";
    var options = new List<DecisionOption> {
      new("A", "Convert", transformedCode, true),
      new("B", "Skip", null, false)
    };
    var point = DecisionPoint.Create("file.cs", 1, "Test", MigrationCategory.Handlers, "original", options);
    point.SelectOption("A");

    // Act
    var code = point.GetSelectedTransformedCode();

    // Assert
    await Assert.That(code).IsEqualTo(transformedCode);
  }

  [Test]
  public async Task GetSelectedTransformedCode_ReturnsNull_WhenSkipSelected_Async() {
    // Arrange
    var options = new List<DecisionOption> {
      new("A", "Convert", "code", true),
      new("B", "Skip", null, false)
    };
    var point = DecisionPoint.Create("file.cs", 1, "Test", MigrationCategory.Handlers, "original", options);
    point.SelectOption("B");

    // Act
    var code = point.GetSelectedTransformedCode();

    // Assert
    await Assert.That(code).IsNull();
  }

  [Test]
  public async Task GetRecommendedOption_ReturnsOptionWithIsRecommendedTrue_Async() {
    // Arrange
    var options = new List<DecisionOption> {
      new("A", "Convert", "code", true),
      new("B", "Skip", null, false)
    };
    var point = DecisionPoint.Create("file.cs", 1, "Test", MigrationCategory.Handlers, "original", options);

    // Act
    var recommended = point.GetRecommendedOption();

    // Assert
    await Assert.That(recommended).IsNotNull();
    await Assert.That(recommended!.Key).IsEqualTo("A");
    await Assert.That(recommended.IsRecommended).IsTrue();
  }

  [Test]
  public async Task FileLocation_ReturnsFormattedFilePath_Async() {
    // Arrange
    var point = DecisionPoint.Create("src/Handlers/OrderHandler.cs", 15, "Test", MigrationCategory.Handlers, "code", []);

    // Act
    var location = point.FileLocation;

    // Assert
    await Assert.That(location).IsEqualTo("src/Handlers/OrderHandler.cs:15");
  }
}
