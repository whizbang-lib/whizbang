using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage-round tests for InboxStrategyPrompt branches not exercised by
/// InboxStrategyPromptTests: rendering the shared-topic and domain-topics examples when no
/// owned domain has been detected yet.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/InboxStrategyPrompt.cs:*</tests>
public class InboxStrategyPromptCoverageTests {

  // A service migrating before domain-ownership detection has found anything still has to see
  // a usable wizard screen. If the no-owned-domain branches were dark, an empty list would
  // either render a blank example or throw when indexing _ownedDomains[0], breaking the very
  // first screen an operator sees.
  [Test]
  public async Task Render_ShowsGenericExamples_WhenNoDomainsAreOwnedAsync() {
    // Arrange
    var prompt = new InboxStrategyPrompt([]);
    var writer = new StringWriter();

    // Act
    prompt.Render(writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("(with filter)")
      .Because("with no owned domain to name, the shared-topic example must fall back to a "
             + "generic placeholder instead of an empty or missing example");
    await Assert.That(output).Contains("\"orders.inbox\"")
      .Because("with no owned domain to name, the domain-topics example must fall back to the "
             + "generic \"orders\" placeholder instead of indexing an empty list");
  }
}
