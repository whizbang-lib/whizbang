using Whizbang.Migrate.Analysis;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the DomainOwnershipPrompt wizard component.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/DomainOwnershipPrompt.cs:*</tests>
public class DomainOwnershipPromptTests {
  [Test]
  public async Task Constructor_PreSelectsMostCommonDomain_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
        new DomainInfo { DomainName = "inventory", OccurrenceCount = 2, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };

    // Act
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Assert
    await Assert.That(prompt.SelectedDomains).Contains("orders");
    await Assert.That(prompt.SelectedDomains.Count).IsEqualTo(1);
  }

  [Test]
  public async Task ToggleDomain_AddsUnselectedDomain_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
        new DomainInfo { DomainName = "inventory", OccurrenceCount = 2, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act - toggle inventory (index 2)
    prompt.ToggleDomain(2);

    // Assert
    await Assert.That(prompt.SelectedDomains).Contains("orders");
    await Assert.That(prompt.SelectedDomains).Contains("inventory");
    await Assert.That(prompt.SelectedDomains.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ToggleDomain_RemovesSelectedDomain_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act - toggle orders (index 1) to deselect
    prompt.ToggleDomain(1);

    // Assert
    await Assert.That(prompt.SelectedDomains).IsEmpty();
  }

  [Test]
  public async Task ProcessInput_AcceptSelection_RetainsCurrentSelection_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act
    var result = prompt.ProcessInput("A");

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(prompt.SelectedDomains).Contains("orders");
  }

  [Test]
  public async Task ProcessInput_None_ClearsSelection_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act
    var result = prompt.ProcessInput("N");

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(prompt.SelectedDomains).IsEmpty();
  }

  [Test]
  public async Task ProcessInput_CommaSeparatedIndices_TogglesMultiple_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
        new DomainInfo { DomainName = "inventory", OccurrenceCount = 2, FromNamespace = true, FromTypeName = false },
        new DomainInfo { DomainName = "shipping", OccurrenceCount = 1, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act - toggle 1 (deselect orders) and 2,3 (select inventory and shipping)
    prompt.ProcessInput("1,2,3");

    // Assert - orders deselected, inventory and shipping selected
    await Assert.That(prompt.SelectedDomains).DoesNotContain("orders");
    await Assert.That(prompt.SelectedDomains).Contains("inventory");
    await Assert.That(prompt.SelectedDomains).Contains("shipping");
  }

  [Test]
  public async Task SetCustomDomains_ReplacesSelection_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act
    prompt.SetCustomDomains(["custom1", "custom2"]);

    // Assert
    await Assert.That(prompt.SelectedDomains).DoesNotContain("orders");
    await Assert.That(prompt.SelectedDomains).Contains("custom1");
    await Assert.That(prompt.SelectedDomains).Contains("custom2");
    await Assert.That(prompt.SelectedDomains.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ApplyTo_SetsRoutingDecisions_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
        new DomainInfo { DomainName = "inventory", OccurrenceCount = 2, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);
    var decisions = new RoutingDecisions();

    // Act
    prompt.ApplyTo(decisions);

    // Assert
    await Assert.That(decisions.OwnedDomains).Contains("orders");
    await Assert.That(decisions.DetectedDomains).Contains("orders");
    await Assert.That(decisions.DetectedDomains).Contains("inventory");
    await Assert.That(decisions.Confirmed).IsTrue();
  }

  [Test]
  public async Task Render_WithNoDetections_ShowsManualConfigMessage_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [],
      MostCommon = null,
      HasDetections = false
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);
    var writer = new StringWriter();

    // Act
    prompt.Render(writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("No domain patterns detected");
    await Assert.That(output).Contains("OwnDomains");
  }

  [Test]
  public async Task Render_WithDetections_ShowsDomainList_Async() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);
    var writer = new StringWriter();

    // Act
    prompt.Render(writer);
    var output = writer.ToString();

    // Assert
    await Assert.That(output).Contains("orders");
    await Assert.That(output).Contains("[x]"); // Pre-selected
    await Assert.That(output).Contains("Recommended");
  }
}
