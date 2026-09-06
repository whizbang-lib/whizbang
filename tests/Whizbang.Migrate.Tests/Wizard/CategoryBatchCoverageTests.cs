using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage-round-23 tests for <see cref="CategoryBatch"/> targeting the default arm of
/// <see cref="CategoryBatch.GetDisplayName"/>.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/CategoryBatch.cs:GetDisplayName</tests>
public class CategoryBatchCoverageTests {

  // The wizard renders this string directly to the CLI operator running a migration. If a
  // MigrationCategory value ever falls outside the five known cases (e.g. read from a
  // DecisionFile written by a newer build than the one running the wizard), this fallback is
  // what keeps the wizard showing a recognizable label instead of an empty string or a throw.
  [Test]
  public async Task GetDisplayName_UndefinedCategory_FallsBackToEnumToStringAsync() {
    // Arrange
    var undefinedCategory = (MigrationCategory)999;

    // Act
    var displayName = CategoryBatch.GetDisplayName(undefinedCategory);

    // Assert
    await Assert.That(displayName).IsEqualTo("999")
      .Because("an enum value with no matching case in the switch must fall back to its own ToString()");
  }
}
