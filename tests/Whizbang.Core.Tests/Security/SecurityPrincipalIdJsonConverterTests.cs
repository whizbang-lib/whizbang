using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for SecurityPrincipalIdJsonConverter.
/// Verifies that SecurityPrincipalId serializes as a string (not an object).
/// </summary>
public class SecurityPrincipalIdJsonConverterTests {
  private static JsonSerializerOptions _createOptions() {
    var options = new JsonSerializerOptions();
    options.Converters.Add(new SecurityPrincipalIdJsonConverter());
    return options;
  }

  [Test]
  public async Task Serialize_SecurityPrincipalId_WritesAsStringAsync() {
    // Arrange
    var principal = SecurityPrincipalId.User("alice");
    var options = _createOptions();

    // Act
    var json = JsonSerializer.Serialize(principal, options);

    // Assert
    await Assert.That(json).IsEqualTo("\"user:alice\"");
  }

  [Test]
  public async Task Deserialize_StringValue_ReturnsSecurityPrincipalIdAsync() {
    // Arrange
    var json = "\"group:sales-team\"";
    var options = _createOptions();

    // Act
    var principal = JsonSerializer.Deserialize<SecurityPrincipalId>(json, options);

    // Assert
    await Assert.That(principal.Value).IsEqualTo("group:sales-team");
    await Assert.That(principal.IsGroup).IsTrue();
  }

  [Test]
  public async Task Serialize_List_WritesAsStringArrayAsync() {
    // Arrange
    var principals = new List<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team"),
      SecurityPrincipalId.Service("api-gateway")
    };
    var options = _createOptions();

    // Act
    var json = JsonSerializer.Serialize(principals, options);

    // Assert
    await Assert.That(json).IsEqualTo("[\"user:alice\",\"group:sales-team\",\"svc:api-gateway\"]");
  }

  [Test]
  public async Task Deserialize_StringArray_ReturnsListAsync() {
    // Arrange
    var json = "[\"user:bob\",\"group:engineering\"]";
    var options = _createOptions();

    // Act
    var principals = JsonSerializer.Deserialize<List<SecurityPrincipalId>>(json, options);

    // Assert
    await Assert.That(principals).IsNotNull();
    await Assert.That(principals!.Count).IsEqualTo(2);
    await Assert.That(principals[0].Value).IsEqualTo("user:bob");
    await Assert.That(principals[1].Value).IsEqualTo("group:engineering");
  }

  [Test]
  public async Task Serialize_PerspectiveScopeWithAllowedPrincipals_WritesCorrectJsonAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456",
      AllowedPrincipals = new List<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("team-A")
      }
    };

    // Use the registered combined options from JsonContextRegistry
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var json = JsonSerializer.Serialize(scope, options);

    // Assert - AllowedPrincipals should be a string array, not object array
    await Assert.That(json).Contains("\"AllowedPrincipals\":[\"user:user-456\",\"group:team-A\"]");
  }

  [Test]
  public async Task Deserialize_PerspectiveScopeWithAllowedPrincipals_ReadsCorrectlyAsync() {
    // Arrange
    var json = """
      {
        "TenantId": "tenant-123",
        "UserId": "user-456",
        "AllowedPrincipals": ["user:user-456", "group:team-A"]
      }
      """;

    // Use the registered combined options from JsonContextRegistry
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act
    var scope = JsonSerializer.Deserialize<PerspectiveScope>(json, options);

    // Assert
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("tenant-123");
    await Assert.That(scope.AllowedPrincipals).IsNotNull();
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(2);
    await Assert.That(scope.AllowedPrincipals[0].Value).IsEqualTo("user:user-456");
    await Assert.That(scope.AllowedPrincipals[1].Value).IsEqualTo("group:team-A");
  }
}
