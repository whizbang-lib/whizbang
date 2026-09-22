using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage-round tests for <see cref="DecisionPoint"/> targeting the branch in
/// GetSelectedTransformedCode that runs before any option has been selected. The primary suite
/// always calls SelectOption first, so this guard is never exercised.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/DecisionPoint.cs:94</tests>
public class DecisionPointCoverageTests {

  // The wizard's preview panel calls GetSelectedTransformedCode() while rendering a decision
  // point, including before the developer has answered it. If this guard were removed,
  // previewing an unanswered decision would throw (Options.Find comparing against a null
  // SelectedOption) instead of returning "no preview yet" -- crashing the wizard mid-migration
  // instead of just waiting for the developer's choice.
  [Test]
  public async Task GetSelectedTransformedCode_NoOptionSelectedYet_ReturnsNullAsync() {
    // Arrange
    var options = new List<DecisionOption> {
      new("A", "Convert", "code", true),
      new("B", "Skip", null, false)
    };
    var point = DecisionPoint.Create("file.cs", 1, "Test", MigrationCategory.Handlers, "original", options);

    // Act -- SelectOption was never called
    var code = point.GetSelectedTransformedCode();

    // Assert
    await Assert.That(code).IsNull()
      .Because("a decision point that hasn't been answered yet has no transformed code to show");
    await Assert.That(point.IsDecided).IsFalse();
  }
}
