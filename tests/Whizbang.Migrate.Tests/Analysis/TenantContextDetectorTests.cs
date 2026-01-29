using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Tests for the TenantContextDetector that scans code for tenant isolation patterns.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/TenantContextDetector.cs:*</tests>
public class TenantContextDetectorTests {
  [Test]
  public async Task DetectAsync_FindsTenantIdProperty_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid OrderId, string TenantId, string CustomerId);
      public record OrderUpdated(Guid OrderId, string TenantId, string Description);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.HasTenantContext).IsTrue();
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("TenantId");
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(2);
  }

  [Test]
  public async Task DetectAsync_FindsScopeProperty_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid OrderId, string Scope, string CustomerId);
      public record OrderUpdated(Guid OrderId, string Scope, string Description);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.HasTenantContext).IsTrue();
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("Scope");
  }

  [Test]
  public async Task DetectAsync_FindsSecurityContextProperty_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid OrderId, SecurityContext SecurityContext, string CustomerId);
      public record OrderUpdated(Guid OrderId, SecurityContext SecurityContext, string Description);

      public record SecurityContext(string TenantId, string UserId);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.HasTenantContext).IsTrue();
    await Assert.That(result.DetectedProperties.Any(p => p.PropertyName == "SecurityContext")).IsTrue();
  }

  [Test]
  public async Task DetectAsync_DetectsMartenForTenantUsage_Async() {
    // Arrange
    var sourceCode = """
      public class OrderService {
        public async Task ProcessOrder(IDocumentSession session, OrderCommand cmd) {
          var tenantSession = session.ForTenant(cmd.TenantId);
          await tenantSession.SaveChangesAsync();
        }
      }
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Services/OrderService.cs");

    // Assert
    await Assert.That(result.UsesMartenTenantFeatures).IsTrue();
    await Assert.That(result.MartenTenantPatterns).Contains("ForTenant");
  }

  [Test]
  public async Task DetectAsync_DetectsMartenTenantedSession_Async() {
    // Arrange
    var sourceCode = """
      public class OrderService {
        public OrderService(IDocumentStore store) {
          _tenantSession = store.OpenSession("tenant-123");
        }
      }
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Services/OrderService.cs");

    // Assert
    await Assert.That(result.UsesMartenTenantFeatures).IsTrue();
    await Assert.That(result.MartenTenantPatterns).Contains("OpenSession with tenant");
  }

  [Test]
  public async Task DetectAsync_RanksMultiplePropertiesByOccurrence_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid OrderId, string TenantId, string CustomerId);
      public record OrderUpdated(Guid OrderId, string TenantId, string Description);
      public record OrderShipped(Guid OrderId, string TenantId, string Carrier);
      public record CustomerCreated(Guid CustomerId, string OrganizationId);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/Events.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(2);
    await Assert.That(result.MostCommon?.PropertyName).IsEqualTo("TenantId");
    await Assert.That(result.MostCommon?.OccurrenceCount).IsEqualTo(3);
  }

  [Test]
  public async Task DetectAsync_IgnoresNonTenantProperties_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid OrderId, string CustomerId, string Description);
      public record OrderUpdated(Guid OrderId, string Name);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.HasTenantContext).IsFalse();
    await Assert.That(result.DetectedProperties).IsEmpty();
  }

  [Test]
  public async Task DetectAsync_RecognizesOrganizationIdAsTenant_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid OrderId, Guid OrganizationId, string CustomerId);
      public record OrderUpdated(Guid OrderId, Guid OrganizationId, string Description);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.HasTenantContext).IsTrue();
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("OrganizationId");
    await Assert.That(result.DetectedProperties[0].IsTenantLike).IsTrue();
  }

  [Test]
  public async Task DetectFromMultipleSourcesAsync_AggregatesAcrossFiles_Async() {
    // Arrange
    var files = new Dictionary<string, string> {
      ["Events/OrderEvents.cs"] = """
        public record OrderCreated(Guid OrderId, string TenantId, string CustomerId);
        public record OrderUpdated(Guid OrderId, string TenantId, string Description);
        """,
      ["Events/CustomerEvents.cs"] = """
        public record CustomerCreated(Guid CustomerId, string TenantId, string Name);
        """,
      ["Services/OrderService.cs"] = """
        public class OrderService {
          public async Task ProcessOrder(IDocumentSession session, OrderCommand cmd) {
            var tenantSession = session.ForTenant(cmd.TenantId);
          }
        }
        """
    };

    // Act
    var result = await TenantContextDetector.DetectFromMultipleSourcesAsync(files);

    // Assert
    await Assert.That(result.HasTenantContext).IsTrue();
    await Assert.That(result.MostCommon?.PropertyName).IsEqualTo("TenantId");
    await Assert.That(result.MostCommon?.OccurrenceCount).IsEqualTo(3);
    await Assert.That(result.UsesMartenTenantFeatures).IsTrue();
  }

  [Test]
  public async Task DetectAsync_DetectsWorkspaceIdAsTenant_Async() {
    // Arrange
    var sourceCode = """
      public record DocumentCreated(Guid DocumentId, Guid WorkspaceId, string Title);
      public record DocumentUpdated(Guid DocumentId, Guid WorkspaceId, string Title);
      """;

    // Act
    var result = await TenantContextDetector.DetectAsync(sourceCode, "Events/DocumentEvents.cs");

    // Assert
    await Assert.That(result.HasTenantContext).IsTrue();
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("WorkspaceId");
    await Assert.That(result.DetectedProperties[0].IsTenantLike).IsTrue();
  }
}
