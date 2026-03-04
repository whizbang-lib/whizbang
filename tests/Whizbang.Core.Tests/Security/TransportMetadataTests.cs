using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for transport metadata interfaces and implementations.
/// Transport metadata provides access to transport-level properties (e.g., Service Bus application properties)
/// that can be used for security context extraction.
/// </summary>
/// <docs>core-concepts/message-security#transport-metadata</docs>
public class TransportMetadataTests {
  // ========================================
  // ITransportMetadata Interface Tests
  // ========================================

  [Test]
  public async Task ITransportMetadata_TransportName_ReturnsTransportIdentifierAsync() {
    // Arrange
    var metadata = new ServiceBusTransportMetadata(new Dictionary<string, object>());

    // Act
    var name = metadata.TransportName;

    // Assert
    await Assert.That(name).IsEqualTo("AzureServiceBus");
  }

  // ========================================
  // ServiceBusTransportMetadata Tests
  // ========================================

  [Test]
  public async Task ServiceBusTransportMetadata_Constructor_NullProperties_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new ServiceBusTransportMetadata(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ServiceBusTransportMetadata_ApplicationProperties_ReturnsProvidedPropertiesAsync() {
    // Arrange
    var properties = new Dictionary<string, object> {
      ["TenantId"] = "tenant-123",
      ["UserId"] = "user-456"
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var result = metadata.ApplicationProperties;

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result["TenantId"]).IsEqualTo("tenant-123");
    await Assert.That(result["UserId"]).IsEqualTo("user-456");
  }

  [Test]
  public async Task ServiceBusTransportMetadata_GetProperty_ExistingKey_ReturnsValueAsync() {
    // Arrange
    var properties = new Dictionary<string, object> {
      ["Authorization"] = "Bearer token123"
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var result = metadata.GetProperty<string>("Authorization");

    // Assert
    await Assert.That(result).IsEqualTo("Bearer token123");
  }

  [Test]
  public async Task ServiceBusTransportMetadata_GetProperty_NonExistingKey_ReturnsDefaultAsync() {
    // Arrange
    var properties = new Dictionary<string, object>();
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var result = metadata.GetProperty<string>("NonExistentKey");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ServiceBusTransportMetadata_GetProperty_WrongType_ReturnsDefaultAsync() {
    // Arrange
    var properties = new Dictionary<string, object> {
      ["Count"] = 42
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var result = metadata.GetProperty<string>("Count");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ServiceBusTransportMetadata_TryGetProperty_ExistingKey_ReturnsTrueAndValueAsync() {
    // Arrange
    var properties = new Dictionary<string, object> {
      ["TenantId"] = "tenant-abc"
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var success = metadata.TryGetProperty<string>("TenantId", out var value);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(value).IsEqualTo("tenant-abc");
  }

  [Test]
  public async Task ServiceBusTransportMetadata_TryGetProperty_NonExistingKey_ReturnsFalseAsync() {
    // Arrange
    var properties = new Dictionary<string, object>();
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var success = metadata.TryGetProperty<string>("Missing", out var value);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(value).IsNull();
  }

  [Test]
  public async Task ServiceBusTransportMetadata_ContainsProperty_ExistingKey_ReturnsTrueAsync() {
    // Arrange
    var properties = new Dictionary<string, object> {
      ["Key1"] = "value"
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var result = metadata.ContainsProperty("Key1");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ServiceBusTransportMetadata_ContainsProperty_NonExistingKey_ReturnsFalseAsync() {
    // Arrange
    var properties = new Dictionary<string, object>();
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var result = metadata.ContainsProperty("Missing");

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ServiceBusTransportMetadata_ApplicationProperties_IsImmutableAsync() {
    // Arrange
    var originalProperties = new Dictionary<string, object> {
      ["Key1"] = "value1"
    };
    var metadata = new ServiceBusTransportMetadata(originalProperties);

    // Act - try to modify original dictionary
    originalProperties["Key2"] = "value2";

    // Assert - metadata should not be affected
    await Assert.That(metadata.ApplicationProperties.Count).IsEqualTo(1);
    await Assert.That(metadata.ContainsProperty("Key2")).IsFalse();
  }

  // ========================================
  // SecurityContext Property Extraction Tests
  // ========================================

  [Test]
  public async Task ServiceBusTransportMetadata_GetProperty_JwtToken_ExtractsTokenAsync() {
    // Arrange - simulate Azure Service Bus application property containing JWT
    var properties = new Dictionary<string, object> {
      ["X-Security-Token"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U"
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var token = metadata.GetProperty<string>("X-Security-Token");

    // Assert
    await Assert.That(token).StartsWith("eyJ");
  }

  [Test]
  public async Task ServiceBusTransportMetadata_GetProperty_TenantAndUser_ExtractsContextAsync() {
    // Arrange - simulate multi-tenant context in Service Bus properties
    var properties = new Dictionary<string, object> {
      ["X-Tenant-Id"] = "tenant-123",
      ["X-User-Id"] = "user-456",
      ["X-Roles"] = "Admin,User"
    };
    var metadata = new ServiceBusTransportMetadata(properties);

    // Act
    var tenantId = metadata.GetProperty<string>("X-Tenant-Id");
    var userId = metadata.GetProperty<string>("X-User-Id");
    var roles = metadata.GetProperty<string>("X-Roles");

    // Assert
    await Assert.That(tenantId).IsEqualTo("tenant-123");
    await Assert.That(userId).IsEqualTo("user-456");
    await Assert.That(roles).IsEqualTo("Admin,User");
  }
}
