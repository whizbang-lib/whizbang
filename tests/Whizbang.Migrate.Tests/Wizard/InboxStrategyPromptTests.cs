using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the InboxStrategyPrompt wizard component.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/InboxStrategyPrompt.cs:*</tests>
public class InboxStrategyPromptTests {
  [Test]
  public async Task Constructor_DefaultsToSharedTopic_Async() {
    // Arrange & Act
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Assert
    await Assert.That(prompt.SelectedStrategy).IsEqualTo(InboxStrategyChoice.SharedTopic);
  }

  [Test]
  public async Task ProcessInput_A_SelectsSharedTopic_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    var result = prompt.ProcessInput("A");

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(prompt.SelectedStrategy).IsEqualTo(InboxStrategyChoice.SharedTopic);
  }

  [Test]
  public async Task ProcessInput_B_SelectsDomainTopics_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    var result = prompt.ProcessInput("B");

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(prompt.SelectedStrategy).IsEqualTo(InboxStrategyChoice.DomainTopics);
  }

  [Test]
  public async Task ProcessInput_LowercaseInput_IsAccepted_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    var result = prompt.ProcessInput("b");

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(prompt.SelectedStrategy).IsEqualTo(InboxStrategyChoice.DomainTopics);
  }

  [Test]
  public async Task ProcessInput_InvalidInput_ReturnsFalse_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    var result = prompt.ProcessInput("X");

    // Assert
    await Assert.That(result).IsFalse();
    // Strategy should remain default
    await Assert.That(prompt.SelectedStrategy).IsEqualTo(InboxStrategyChoice.SharedTopic);
  }

  [Test]
  public async Task ProcessInput_NullInput_ReturnsFalse_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    var result = prompt.ProcessInput(null);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ProcessInput_EmptyInput_ReturnsFalse_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    var result = prompt.ProcessInput("");

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task SetCustomTopic_SetsCustomTopic_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    prompt.SetCustomTopic("my.custom.inbox");

    // Assert
    await Assert.That(prompt.CustomTopic).IsEqualTo("my.custom.inbox");
  }

  [Test]
  public async Task SetCustomSuffix_SetsCustomSuffix_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);

    // Act
    prompt.SetCustomSuffix(".in");

    // Assert
    await Assert.That(prompt.CustomSuffix).IsEqualTo(".in");
  }

  [Test]
  public async Task ApplyTo_SetsRoutingDecisions_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);
    prompt.ProcessInput("B");
    prompt.SetCustomSuffix(".in");
    var decisions = new RoutingDecisions();

    // Act
    prompt.ApplyTo(decisions);

    // Assert
    await Assert.That(decisions.InboxStrategy).IsEqualTo(InboxStrategyChoice.DomainTopics);
    await Assert.That(decisions.InboxSuffix).IsEqualTo(".in");
  }

  [Test]
  public async Task Render_ShowsBothOptions_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);
    var writer = new StringWriter();

    // Act
    prompt.Render(writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("Shared Topic");
    await Assert.That(output).Contains("Domain Topics");
    await Assert.That(output).Contains("[A]");
    await Assert.That(output).Contains("[B]");
    await Assert.That(output).Contains("Recommended");
  }

  [Test]
  public async Task Render_ShowsExampleWithOwnedDomain_Async() {
    // Arrange
    var prompt = new InboxStrategyPrompt(["orders"]);
    var writer = new StringWriter();

    // Act
    prompt.Render(writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("orders.inbox");
  }
}
