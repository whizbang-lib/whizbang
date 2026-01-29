using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the SecurityPrincipalId value object.
/// </summary>
/// <tests>SecurityPrincipalId</tests>
public class SecurityPrincipalIdTests {
  // === Constructor Tests ===

  [Test]
  public async Task SecurityPrincipalId_Constructor_ValidValue_CreatesInstanceAsync() {
    // Arrange & Act
    var principal = new SecurityPrincipalId("user:alice");

    // Assert
    await Assert.That(principal.Value).IsEqualTo("user:alice");
  }

  // === Factory Method Tests ===

  [Test]
  public async Task SecurityPrincipalId_User_HasCorrectPrefixAsync() {
    // Arrange & Act
    var principal = SecurityPrincipalId.User("alice");

    // Assert
    await Assert.That(principal.Value).IsEqualTo("user:alice");
  }

  [Test]
  public async Task SecurityPrincipalId_Group_HasCorrectPrefixAsync() {
    // Arrange & Act
    var principal = SecurityPrincipalId.Group("sales-team");

    // Assert
    await Assert.That(principal.Value).IsEqualTo("group:sales-team");
  }

  [Test]
  public async Task SecurityPrincipalId_Service_HasCorrectPrefixAsync() {
    // Arrange & Act
    var principal = SecurityPrincipalId.Service("payment-processor");

    // Assert
    await Assert.That(principal.Value).IsEqualTo("svc:payment-processor");
  }

  [Test]
  public async Task SecurityPrincipalId_Application_HasCorrectPrefixAsync() {
    // Arrange & Act
    var principal = SecurityPrincipalId.Application("mobile-app");

    // Assert
    await Assert.That(principal.Value).IsEqualTo("app:mobile-app");
  }

  // === Type Detection Tests ===

  [Test]
  public async Task SecurityPrincipalId_IsUser_ReturnsTrueForUserPrefixAsync() {
    // Arrange
    var principal = SecurityPrincipalId.User("alice");

    // Act & Assert
    await Assert.That(principal.IsUser).IsTrue();
    await Assert.That(principal.IsGroup).IsFalse();
    await Assert.That(principal.IsService).IsFalse();
    await Assert.That(principal.IsApplication).IsFalse();
  }

  [Test]
  public async Task SecurityPrincipalId_IsGroup_ReturnsTrueForGroupPrefixAsync() {
    // Arrange
    var principal = SecurityPrincipalId.Group("admins");

    // Act & Assert
    await Assert.That(principal.IsGroup).IsTrue();
    await Assert.That(principal.IsUser).IsFalse();
    await Assert.That(principal.IsService).IsFalse();
    await Assert.That(principal.IsApplication).IsFalse();
  }

  [Test]
  public async Task SecurityPrincipalId_IsService_ReturnsTrueForServicePrefixAsync() {
    // Arrange
    var principal = SecurityPrincipalId.Service("api-gateway");

    // Act & Assert
    await Assert.That(principal.IsService).IsTrue();
    await Assert.That(principal.IsUser).IsFalse();
    await Assert.That(principal.IsGroup).IsFalse();
    await Assert.That(principal.IsApplication).IsFalse();
  }

  [Test]
  public async Task SecurityPrincipalId_IsApplication_ReturnsTrueForApplicationPrefixAsync() {
    // Arrange
    var principal = SecurityPrincipalId.Application("web-portal");

    // Act & Assert
    await Assert.That(principal.IsApplication).IsTrue();
    await Assert.That(principal.IsUser).IsFalse();
    await Assert.That(principal.IsGroup).IsFalse();
    await Assert.That(principal.IsService).IsFalse();
  }

  // === Implicit Conversion Tests ===

  [Test]
  public async Task SecurityPrincipalId_ImplicitToString_ReturnsValueAsync() {
    // Arrange
    var principal = SecurityPrincipalId.User("bob");

    // Act
    string value = principal;

    // Assert
    await Assert.That(value).IsEqualTo("user:bob");
  }

  [Test]
  public async Task SecurityPrincipalId_ImplicitFromString_CreatesPrincipalIdAsync() {
    // Arrange & Act
    SecurityPrincipalId principal = "group:developers";

    // Assert
    await Assert.That(principal.Value).IsEqualTo("group:developers");
  }

  // === ToString Tests ===

  [Test]
  public async Task SecurityPrincipalId_ToString_ReturnsValueAsync() {
    // Arrange
    var principal = SecurityPrincipalId.Group("managers");

    // Act
    var result = principal.ToString();

    // Assert
    await Assert.That(result).IsEqualTo("group:managers");
  }

  // === Equality Tests ===

  [Test]
  public async Task SecurityPrincipalId_Equals_SameValue_ReturnsTrueAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.User("alice");
    var principal2 = SecurityPrincipalId.User("alice");

    // Act & Assert
    await Assert.That(principal1).IsEqualTo(principal2);
  }

  [Test]
  public async Task SecurityPrincipalId_Equals_DifferentValue_ReturnsFalseAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.User("alice");
    var principal2 = SecurityPrincipalId.User("bob");

    // Act & Assert
    await Assert.That(principal1).IsNotEqualTo(principal2);
  }

  [Test]
  public async Task SecurityPrincipalId_Equals_DifferentType_ReturnsFalseAsync() {
    // Arrange
    var user = SecurityPrincipalId.User("alice");
    var group = SecurityPrincipalId.Group("alice");

    // Act & Assert
    await Assert.That(user).IsNotEqualTo(group);
  }

  [Test]
  public async Task SecurityPrincipalId_GetHashCode_SameValue_ReturnsSameHashAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.User("alice");
    var principal2 = SecurityPrincipalId.User("alice");

    // Act & Assert
    await Assert.That(principal1.GetHashCode()).IsEqualTo(principal2.GetHashCode());
  }

  // === HashSet Usage Tests ===

  [Test]
  public async Task SecurityPrincipalId_InHashSet_SupportsContainsCheckAsync() {
    // Arrange
    var principals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team"),
      SecurityPrincipalId.Group("all-employees")
    };

    // Act & Assert
    await Assert.That(principals.Contains(SecurityPrincipalId.User("alice"))).IsTrue();
    await Assert.That(principals.Contains(SecurityPrincipalId.Group("sales-team"))).IsTrue();
    await Assert.That(principals.Contains(SecurityPrincipalId.User("bob"))).IsFalse();
  }
}
