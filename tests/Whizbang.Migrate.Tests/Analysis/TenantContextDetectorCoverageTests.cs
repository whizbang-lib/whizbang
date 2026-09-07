using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Coverage-focused tests for <see cref="TenantContextDetector"/> targeting branches the primary
/// test suite does not reach: ranking two distinct tenant properties aggregated across multiple
/// source files, and a body-syntax record (no positional parameter list) that matches the
/// event-name heuristic.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/TenantContextDetector.cs:*</tests>
public class TenantContextDetectorCoverageTests {
  // If cross-file aggregation stopped ranking properties by occurrence count, a migration
  // report could recommend the wrong tenant property as "most common" when scanning a real
  // codebase split across many event files -- exactly the shape DetectFromMultipleSourcesAsync
  // exists to handle.
  [Test]
  public async Task DetectFromMultipleSourcesAsync_RanksAggregatedPropertiesByOccurrence_Async() {
    // Arrange
    var files = new Dictionary<string, string> {
      ["Events/OrderEvents.cs"] = """
        public record OrderCreated(Guid OrderId, string TenantId, string CustomerId);
        public record OrderUpdated(Guid OrderId, string TenantId, string Description);
        """,
      ["Events/CustomerEvents.cs"] = """
        public record CustomerCreated(Guid CustomerId, string OrganizationId, string Name);
        """
    };

    // Act
    var result = await TenantContextDetector.DetectFromMultipleSourcesAsync(files);

    // Assert - TenantId (count 2) ranks above OrganizationId (count 1) after aggregation.
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(2);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("TenantId");
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(2);
    await Assert.That(result.DetectedProperties[1].PropertyName).IsEqualTo("OrganizationId");
    await Assert.That(result.MostCommon?.PropertyName).IsEqualTo("TenantId");
  }

  // If a body-syntax record (properties instead of a positional parameter list) were treated
  // the same as a positional record, a real codebase using that style would either crash
  // (no ParameterList to read) or silently produce wrong occurrence counts; today it is
  // silently skipped, so a TenantId declared this way never surfaces to the migration wizard.
  [Test]
  public async Task DetectAsync_BodySyntaxRecordWithoutPositionalParameters_IsSkippedAsync() {
    // Arrange
    const string sourceCode = """
      public record OrderStatusChanged {
        public string TenantId { get; init; } = "";
      }
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/OrderStatusChanged.cs");

    // Assert - the record name matches the event heuristic (ends with "Changed"), but with no
    // ParameterList there are no parameters to inspect, so TenantId is never detected.
    await Assert.That(result.HasTenantContext).IsFalse();
    await Assert.That(result.DetectedProperties).IsEmpty();
  }
}
