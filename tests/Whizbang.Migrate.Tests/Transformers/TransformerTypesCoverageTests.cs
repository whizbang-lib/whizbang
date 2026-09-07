using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage-round tests for the <see cref="CodeChange"/> record's OriginalText/NewText
/// properties. No existing test reads either property directly, even though the migration
/// CLI's diff-style change log (Whizbang.CLI Program.cs) prints exactly these two fields for
/// every recorded change.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/TransformerTypes.cs:28,29</tests>
public class TransformerTypesCoverageTests {

  // The CLI prints "-  {OriginalText}" / "+  {NewText}" for every change so a developer can
  // review a migration before applying it. If either field silently held the wrong text (or
  // came back blank), the printed diff would misrepresent what the tool actually rewrote --
  // someone could approve a change to their codebase they never actually saw.
  [Test]
  public async Task CodeChange_RecordsOriginalAndNewTextForARealRewriteAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    const string sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");
    var change = result.Changes.Single(c => c.ChangeType == ChangeType.MethodCallReplacement);

    // Assert
    await Assert.That(change.OriginalText).IsEqualTo("Guid.NewGuid()")
      .Because("the diff log's '-' line must show exactly what the developer's code used to say");
    await Assert.That(change.NewText).IsEqualTo("idProvider.NewGuid()")
      .Because("the diff log's '+' line must show exactly what the tool replaced it with");
  }
}
