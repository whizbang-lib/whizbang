using Whizbang.Migrate.Analysis;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage-focused tests for <see cref="DomainOwnershipPrompt"/> targeting input-validation
/// branches the primary test suite does not reach: blank input, the "Custom" command, and an
/// out-of-range toggle index.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/DomainOwnershipPrompt.cs:*</tests>
public class DomainOwnershipPromptCoverageTests {
  // If ProcessInput stopped rejecting blank/whitespace input, the migration wizard would treat
  // a stray keystroke as a "handled" command and silently move on instead of re-prompting the
  // operator for a real selection.
  [Test]
  public async Task ProcessInput_NullOrWhitespaceInput_ReturnsFalseAsync() {
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
    var blankResult = prompt.ProcessInput("   ");
    var nullResult = prompt.ProcessInput(null);

    // Assert - neither call is treated as handled, and selection state is untouched.
    await Assert.That(blankResult).IsFalse();
    await Assert.That(nullResult).IsFalse();
    await Assert.That(prompt.SelectedDomains).Contains("orders");
  }

  // If ProcessInput stopped returning false for "C", the caller would never learn to switch
  // into its own custom-domain-name prompt, leaving the wizard stuck on the checklist screen.
  [Test]
  public async Task ProcessInput_CustomCommand_ReturnsFalseAsync() {
    // Arrange
    var detectionResult = new DomainDetectionResult {
      DetectedDomains = [
        new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false }
      ],
      MostCommon = new DomainInfo { DomainName = "orders", OccurrenceCount = 5, FromNamespace = true, FromTypeName = false },
      HasDetections = true
    };
    var prompt = new DomainOwnershipPrompt(detectionResult);

    // Act - "C" (Custom) is handled by the caller, not by ProcessInput itself.
    var result = prompt.ProcessInput("c");

    // Assert
    await Assert.That(result).IsFalse();
    await Assert.That(prompt.SelectedDomains).Contains("orders");
  }

  // If ToggleDomain stopped rejecting an out-of-range index, a mistyped selection number
  // (e.g. "9" when only two domains were detected) would throw instead of being ignored,
  // crashing the interactive wizard on a simple typo.
  [Test]
  public async Task ToggleDomain_IndexOutOfRange_ReturnsFalseAsync() {
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
    var tooLow = prompt.ToggleDomain(0);
    var tooHigh = prompt.ToggleDomain(2);

    // Assert - both out-of-range indices are rejected and selection is untouched.
    await Assert.That(tooLow).IsFalse();
    await Assert.That(tooHigh).IsFalse();
    await Assert.That(prompt.SelectedDomains).Contains("orders");
  }
}
