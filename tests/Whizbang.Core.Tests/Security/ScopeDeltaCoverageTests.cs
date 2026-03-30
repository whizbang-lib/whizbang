using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

// Suppress CA1861 for test file - constant array arguments are acceptable in test code
#pragma warning disable CA1861

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Additional coverage tests for ScopeDelta, CollectionChanges, and ScopePropJsonConverter.
/// Focuses on uncovered code paths: edge cases, error paths, and full round-trip scenarios.
/// </summary>
/// <tests>Whizbang.Core/Security/ScopeDelta.cs</tests>
/// <tests>Whizbang.Core/Security/ScopePropJsonConverter.cs</tests>
[Category("Security")]
[Category("ScopeDelta")]
public class ScopeDeltaCoverageTests {

  #region ScopePropJsonConverter - Error Paths

  private static readonly JsonSerializerOptions _converterOptions = new() {
    Converters = { new ScopePropJsonConverter() }
  };

  [Test]
  public async Task ScopePropJsonConverter_Read_NullOrEmpty_ThrowsJsonExceptionAsync() {
    // Arrange - JSON with null value for enum
    const string json = "null";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<ScopeProp>(json, _converterOptions))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ScopePropJsonConverter_Read_UnknownValue_ThrowsJsonExceptionAsync() {
    // Arrange - JSON with unknown abbreviated name
    var json = "\"Zz\"";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<ScopeProp>(json, _converterOptions))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ScopePropJsonConverter_ReadAsPropertyName_UnknownValue_ThrowsJsonExceptionAsync() {
    // Arrange - Dictionary JSON with unknown key
    var json = """{"Zz":"value"}""";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _converterOptions))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ScopePropJsonConverter_ReadAsPropertyName_EmptyString_ThrowsJsonExceptionAsync() {
    // Arrange - Dictionary JSON with empty key
    var json = """{"":"value"}""";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _converterOptions))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ScopePropJsonConverter_Read_EmptyString_ThrowsJsonExceptionAsync() {
    // Arrange - JSON with empty string value
    var json = "\"\"";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<ScopeProp>(json, _converterOptions))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ScopePropJsonConverter_ReadAsPropertyName_FullName_FallsBackToEnumParseAsync() {
    // Arrange - Dictionary JSON with full enum names as keys (backward compat)
    var json = """{"Scope":"val1","Roles":"val2","Perms":"val3","Principals":"val4","Claims":"val5","Actual":"val6","Effective":"val7","Type":"val8"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _converterOptions);

    // Assert - All keys deserialized via Enum.TryParse fallback
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.Count).IsEqualTo(8);
    await Assert.That(dict.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Roles)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Perms)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Principals)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Claims)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Actual)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Effective)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Type)).IsTrue();
  }

  [Test]
  public async Task ScopePropJsonConverter_ReadAsPropertyName_CaseInsensitive_SucceedsAsync() {
    // Arrange - Dictionary JSON with lowercase full name (tests case insensitive parse)
    var json = """{"scope":"val1","roles":"val2"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _converterOptions);

    // Assert
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Roles)).IsTrue();
  }

  #endregion

  #region CollectionChanges.HasChanges - Edge Cases

  [Test]
  public async Task CollectionChanges_HasChanges_OnlyAdd_ReturnsTrueAsync() {
    // Arrange
    var changes = new CollectionChanges {
      Add = JsonSerializer.SerializeToElement(new[] { "item1" })
    };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
  }

  [Test]
  public async Task CollectionChanges_HasChanges_OnlyRemove_ReturnsTrueAsync() {
    // Arrange
    var changes = new CollectionChanges {
      Remove = JsonSerializer.SerializeToElement(new[] { "item1" })
    };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
  }

  [Test]
  public async Task CollectionChanges_HasChanges_OnlySet_ReturnsTrueAsync() {
    // Arrange
    var changes = new CollectionChanges {
      Set = JsonSerializer.SerializeToElement(new[] { "item1" })
    };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
  }

  [Test]
  public async Task CollectionChanges_HasChanges_AllNull_ReturnsFalseAsync() {
    // Arrange
    var changes = new CollectionChanges();

    // Assert
    await Assert.That(changes.HasChanges).IsFalse();
  }

  #endregion

  #region CreateDelta + ApplyTo Full Round-Trip With All Properties

  [Test]
  public async Task CreateDelta_ApplyTo_FullRoundTrip_AllPropertiesChangedAsync() {
    // Arrange - previous with all properties set
    var previousScope = new PerspectiveScope {
      TenantId = "old-tenant",
      UserId = "old-user",
      CustomerId = "old-customer",
      OrganizationId = "old-org",
      AllowedPrincipals = ["user:old-alice"]
    };
    var previous = new ScopeContext {
      Scope = previousScope,
      Roles = new HashSet<string> { "OldRole" },
      Permissions = new HashSet<Permission> { Permission.Read("old-resource") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("old-user") },
      Claims = new Dictionary<string, string> { ["old-claim"] = "old-value" },
      ActualPrincipal = "old-actual@test.com",
      EffectivePrincipal = "old-effective@test.com",
      ContextType = SecurityContextType.User
    };

    // Current with all properties different
    var currentScope = new PerspectiveScope {
      TenantId = "new-tenant",
      UserId = "new-user",
      CustomerId = "new-customer",
      OrganizationId = "new-org",
      AllowedPrincipals = ["user:new-bob", "group:new-team"]
    };
    var current = new ScopeContext {
      Scope = currentScope,
      Roles = new HashSet<string> { "NewRole1", "NewRole2" },
      Permissions = new HashSet<Permission> { Permission.Write("new-resource") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.Group("new-group") },
      Claims = new Dictionary<string, string> { ["new-claim"] = "new-value" },
      ActualPrincipal = "new-actual@test.com",
      EffectivePrincipal = "new-effective@test.com",
      ContextType = SecurityContextType.Impersonated
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert - all properties updated
    await Assert.That(applied.Scope.TenantId).IsEqualTo("new-tenant");
    await Assert.That(applied.Scope.UserId).IsEqualTo("new-user");
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("new-customer");
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("new-org");
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(2);
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("user:new-bob");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("group:new-team");
    await Assert.That(applied.Roles).Contains("NewRole1");
    await Assert.That(applied.Roles).Contains("NewRole2");
    await Assert.That(applied.Roles).DoesNotContain("OldRole");
    await Assert.That(applied.Permissions).Contains(Permission.Write("new-resource"));
    await Assert.That(applied.Permissions).DoesNotContain(Permission.Read("old-resource"));
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.Group("new-group"));
    await Assert.That(applied.SecurityPrincipals).DoesNotContain(SecurityPrincipalId.User("old-user"));
    await Assert.That(applied.Claims.ContainsKey("new-claim")).IsTrue();
    await Assert.That(applied.Claims["new-claim"]).IsEqualTo("new-value");
    await Assert.That(applied.Claims.ContainsKey("old-claim")).IsFalse();
    await Assert.That(applied.ActualPrincipal).IsEqualTo("new-actual@test.com");
    await Assert.That(applied.EffectivePrincipal).IsEqualTo("new-effective@test.com");
    await Assert.That(applied.ContextType).IsEqualTo(SecurityContextType.Impersonated);
  }

  [Test]
  public async Task CreateDelta_ApplyTo_FromNull_AllPropertiesSetAsync() {
    // Arrange - Create from null previous with every property populated
    var currentScope = new PerspectiveScope {
      TenantId = "t1",
      UserId = "u1",
      CustomerId = "c1",
      OrganizationId = "o1",
      AllowedPrincipals = ["user:alice", "group:team-a", "group:team-b"]
    };
    var current = new ScopeContext {
      Scope = currentScope,
      Roles = new HashSet<string> { "Admin", "Manager", "Viewer" },
      Permissions = new HashSet<Permission> {
        Permission.Read("orders"),
        Permission.Write("orders"),
        Permission.Delete("products")
      },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("u1"),
        SecurityPrincipalId.Group("team-a")
      },
      Claims = new Dictionary<string, string> {
        ["sub"] = "u1",
        ["email"] = "alice@test.com",
        ["aud"] = "api"
      },
      ActualPrincipal = "alice@test.com",
      EffectivePrincipal = "alice@test.com",
      ContextType = SecurityContextType.System
    };

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(applied.Scope.UserId).IsEqualTo("u1");
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("c1");
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("o1");
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(3);
    await Assert.That(applied.Roles.Count).IsEqualTo(3);
    await Assert.That(applied.Permissions.Count).IsEqualTo(3);
    await Assert.That(applied.SecurityPrincipals.Count).IsEqualTo(2);
    await Assert.That(applied.Claims.Count).IsEqualTo(3);
    await Assert.That(applied.ActualPrincipal).IsEqualTo("alice@test.com");
    await Assert.That(applied.EffectivePrincipal).IsEqualTo("alice@test.com");
    await Assert.That(applied.ContextType).IsEqualTo(SecurityContextType.System);
  }

  #endregion

  #region _serializeScope / _deserializeScope - Edge Cases

  [Test]
  public async Task CreateDelta_ApplyTo_ScopeWithOnlyCustomerId_PreservedAsync() {
    // Arrange - Scope with only CustomerId set (no TenantId/UserId)
    var currentScope = new PerspectiveScope { CustomerId = "cust-only" };
    var current = _createScopeContextFromScope(currentScope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("cust-only");
    await Assert.That(applied.Scope.TenantId).IsNull();
    await Assert.That(applied.Scope.UserId).IsNull();
    await Assert.That(applied.Scope.OrganizationId).IsNull();
  }

  [Test]
  public async Task CreateDelta_ApplyTo_ScopeWithOnlyOrganizationId_PreservedAsync() {
    // Arrange - Scope with only OrganizationId set
    var currentScope = new PerspectiveScope { OrganizationId = "org-only" };
    var current = _createScopeContextFromScope(currentScope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("org-only");
    await Assert.That(applied.Scope.TenantId).IsNull();
  }

  [Test]
  public async Task CreateDelta_ApplyTo_ScopeWithOnlyAllowedPrincipals_PreservedAsync() {
    // Arrange - Scope with only AllowedPrincipals (no standard IDs)
    var currentScope = new PerspectiveScope {
      AllowedPrincipals = ["user:x", "group:y"]
    };
    var current = _createScopeContextFromScope(currentScope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(2);
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("user:x");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("group:y");
    await Assert.That(applied.Scope.TenantId).IsNull();
  }

  [Test]
  public async Task CreateDelta_ApplyTo_EmptyScope_RoundTripsAsync() {
    // Arrange - Completely empty scope
    var currentScope = new PerspectiveScope();
    var current = _createScopeContextFromScope(currentScope);

    // Act - From null previous, empty scope still creates delta for contextType
    var delta = ScopeDelta.CreateDelta(null, current);

    // Assert - delta might be null if nothing changed from defaults
    if (delta != null) {
      var applied = delta.ApplyTo(null);
      await Assert.That(applied.Scope.TenantId).IsNull();
      await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(0);
    }
  }

  #endregion

  #region _serializeString / _deserializeString - Null Paths

  [Test]
  public async Task CreateDelta_NullActualPrincipal_ToNonNull_CapturesChangeAsync() {
    // Arrange - ActualPrincipal from null to a value
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ActualPrincipal = null
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ActualPrincipal = "admin@test.com"
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.ActualPrincipal).IsEqualTo("admin@test.com");
  }

  [Test]
  public async Task CreateDelta_NonNullActualPrincipal_ToNull_CapturesChangeAsync() {
    // Arrange - ActualPrincipal from a value to null
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ActualPrincipal = "admin@test.com"
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ActualPrincipal = null
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert - null serialization/deserialization path
    await Assert.That(applied.ActualPrincipal).IsNull();
  }

  [Test]
  public async Task CreateDelta_NullEffectivePrincipal_ToNonNull_CapturesChangeAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      EffectivePrincipal = null
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      EffectivePrincipal = "impersonated@test.com"
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.EffectivePrincipal).IsEqualTo("impersonated@test.com");
  }

  [Test]
  public async Task ApplyTo_NullEffectivePrincipal_DeserializesNullAsync() {
    // Arrange - Test _deserializeString null path for EffectivePrincipal
    using var doc = JsonDocument.Parse("null");
    var nullElement = doc.RootElement.Clone();
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Effective] = nullElement
      }
    };

    // Act
    var result = delta.ApplyTo(null);

    // Assert
    await Assert.That(result.EffectivePrincipal).IsNull();
  }

  #endregion

  #region _createCollectionChanges - Partial Add/Remove (not full replacement)

  [Test]
  public async Task CreateDelta_RolesPartialAddOnly_UsesAddNotSetAsync() {
    // Arrange - Add roles to existing set (not full replacement)
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string> { "User", "Viewer" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string> { "User", "Viewer", "Editor" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Should use Add operation, not Set (partial change)
    await Assert.That(delta).IsNotNull();
    var rolesChanges = delta!.Collections![ScopeProp.Roles];
    await Assert.That(rolesChanges.Add).IsNotNull();
    await Assert.That(rolesChanges.Set).IsNull();
    await Assert.That(rolesChanges.Remove).IsNull();

    // Verify round-trip
    var applied = delta.ApplyTo(previous);
    await Assert.That(applied.Roles.Count).IsEqualTo(3);
    await Assert.That(applied.Roles).Contains("Editor");
  }

  [Test]
  public async Task CreateDelta_RolesPartialRemoveOnly_UsesRemoveNotSetAsync() {
    // Arrange - Remove some roles from existing set
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string> { "User", "Viewer", "Editor" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Should use Remove operation
    await Assert.That(delta).IsNotNull();
    var rolesChanges = delta!.Collections![ScopeProp.Roles];
    await Assert.That(rolesChanges.Remove).IsNotNull();
    await Assert.That(rolesChanges.Set).IsNull();

    // Verify round-trip
    var applied = delta.ApplyTo(previous);
    await Assert.That(applied.Roles.Count).IsEqualTo(1);
    await Assert.That(applied.Roles).Contains("User");
  }

  [Test]
  public async Task CreateDelta_PermissionsPartialAdd_UsesAddOperationAsync() {
    // Arrange - Add a permission (not full replacement)
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders"), Permission.Write("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    var permsChanges = delta!.Collections![ScopeProp.Perms];
    await Assert.That(permsChanges.Add).IsNotNull();
    await Assert.That(permsChanges.Set).IsNull();

    // Verify round-trip
    var applied = delta.ApplyTo(previous);
    await Assert.That(applied.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(applied.Permissions).Contains(Permission.Write("orders"));
  }

  [Test]
  public async Task CreateDelta_PrincipalsPartialAdd_UsesAddOperationAsync() {
    // Arrange - Add a principal (not full replacement)
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("u1") },
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("u1"),
        SecurityPrincipalId.Group("dev-team")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    var principalsChanges = delta!.Collections![ScopeProp.Principals];
    await Assert.That(principalsChanges.Add).IsNotNull();
    await Assert.That(principalsChanges.Set).IsNull();

    // Verify round-trip
    var applied = delta.ApplyTo(previous);
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.User("u1"));
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.Group("dev-team"));
  }

  [Test]
  public async Task CreateDelta_PermissionsRemoved_UsesRemoveOperationAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> {
        Permission.Read("orders"),
        Permission.Write("orders"),
        Permission.Delete("orders")
      },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Permissions.Count).IsEqualTo(1);
    await Assert.That(applied.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(applied.Permissions).DoesNotContain(Permission.Write("orders"));
  }

  [Test]
  public async Task CreateDelta_PrincipalsRemoved_UsesRemoveOperationAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("u1"),
        SecurityPrincipalId.Group("team-a"),
        SecurityPrincipalId.Group("team-b")
      },
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("u1") },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.SecurityPrincipals.Count).IsEqualTo(1);
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.User("u1"));
  }

  #endregion

  #region _createClaimsChanges / _applyClaimsChanges - Comprehensive

  [Test]
  public async Task CreateDelta_ClaimsAddedModifiedRemoved_AllCapturedAsync() {
    // Arrange - Tests all three paths: add new, modify existing, remove old
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previousClaims = new Dictionary<string, string> {
      ["sub"] = "user-1",
      ["role"] = "user",
      ["old-claim"] = "to-be-removed"
    };
    var currentClaims = new Dictionary<string, string> {
      ["sub"] = "user-1",        // unchanged
      ["role"] = "admin",        // modified
      ["new-claim"] = "added"    // added
      // "old-claim" removed
    };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = previousClaims
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = currentClaims
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Claims changes captured
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    var claimsChanges = delta.Collections![ScopeProp.Claims];
    await Assert.That(claimsChanges.Add).IsNotNull();   // modified + added
    await Assert.That(claimsChanges.Remove).IsNotNull(); // removed

    // Verify round-trip
    var applied = delta.ApplyTo(previous);
    await Assert.That(applied.Claims["sub"]).IsEqualTo("user-1");     // unchanged
    await Assert.That(applied.Claims["role"]).IsEqualTo("admin");     // modified
    await Assert.That(applied.Claims["new-claim"]).IsEqualTo("added"); // added
    await Assert.That(applied.Claims.ContainsKey("old-claim")).IsFalse(); // removed
  }

  [Test]
  public async Task CreateDelta_ClaimsFromEmpty_CapturesAllAsAddAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> {
        ["sub"] = "u1",
        ["email"] = "test@test.com"
      }
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Claims.Count).IsEqualTo(2);
    await Assert.That(applied.Claims["sub"]).IsEqualTo("u1");
    await Assert.That(applied.Claims["email"]).IsEqualTo("test@test.com");
  }

  [Test]
  public async Task CreateDelta_ClaimsAllRemoved_CapturesRemovalsAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> {
        ["sub"] = "u1",
        ["email"] = "test@test.com"
      }
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Claims.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CreateDelta_ClaimsFromNull_CapturesAllAsAddAsync() {
    // Arrange - Previous is null (first hop), current has claims
    var currentScope = new PerspectiveScope { TenantId = "t1" };
    var current = new ScopeContext {
      Scope = currentScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> {
        ["sub"] = "u1",
        ["iss"] = "auth-server",
        ["aud"] = "api"
      }
    };

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Claims.Count).IsEqualTo(3);
    await Assert.That(applied.Claims["sub"]).IsEqualTo("u1");
    await Assert.That(applied.Claims["iss"]).IsEqualTo("auth-server");
    await Assert.That(applied.Claims["aud"]).IsEqualTo("api");
  }

  #endregion

  #region JSON Serialization Round-Trip - Full ScopeDelta with Claims

  [Test]
  public async Task ScopeDelta_WithClaimsChanges_SerializationRoundTripAsync() {
    // Arrange - Create a delta with claims add and remove via CreateDelta
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previousClaims = new Dictionary<string, string> { ["old"] = "val", ["keep"] = "same" };
    var currentClaims = new Dictionary<string, string> { ["keep"] = "same", ["new"] = "added" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = previousClaims
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = currentClaims
    };
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Act - Serialize and deserialize
    var json = JsonSerializer.Serialize(delta);
    var deserialized = JsonSerializer.Deserialize<ScopeDelta>(json);

    // Apply deserialized delta
    var applied = deserialized!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Claims.ContainsKey("keep")).IsTrue();
    await Assert.That(applied.Claims["keep"]).IsEqualTo("same");
    await Assert.That(applied.Claims.ContainsKey("new")).IsTrue();
    await Assert.That(applied.Claims["new"]).IsEqualTo("added");
    await Assert.That(applied.Claims.ContainsKey("old")).IsFalse();
  }

  [Test]
  public async Task ScopeDelta_WithAllCollectionTypes_SerializationRoundTripAsync() {
    // Arrange - Create delta with roles, perms, principals, claims all changed
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("u1") },
      Claims = new Dictionary<string, string> { ["sub"] = "u1" }
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string> { "User", "Admin" },
      Permissions = new HashSet<Permission> { Permission.Read("orders"), Permission.Write("products") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("u1"),
        SecurityPrincipalId.Group("team")
      },
      Claims = new Dictionary<string, string> { ["sub"] = "u1", ["role"] = "admin" }
    };

    var delta = ScopeDelta.CreateDelta(previous, current);

    // Act - Round-trip through JSON
    var json = JsonSerializer.Serialize(delta);
    var deserialized = JsonSerializer.Deserialize<ScopeDelta>(json);
    var applied = deserialized!.ApplyTo(previous);

    // Assert - All collection changes applied
    await Assert.That(applied.Roles).Contains("Admin");
    await Assert.That(applied.Permissions).Contains(Permission.Write("products"));
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.Group("team"));
    await Assert.That(applied.Claims["role"]).IsEqualTo("admin");
  }

  #endregion

  #region ApplyTo - Permissions and Principals with Remove then Add

  [Test]
  public async Task ApplyTo_PermissionsRemoveThenAdd_AppliedInCorrectOrderAsync() {
    // Arrange - Manually construct delta with both remove and add for permissions
    var previous = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "t1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> {
        Permission.Read("orders"),
        Permission.Write("orders")
      },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    var addPerms = JsonSerializer.SerializeToElement(new[] { "products:delete" });
    var removePerms = JsonSerializer.SerializeToElement(new[] { "orders:write" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Perms] = new CollectionChanges { Add = addPerms, Remove = removePerms }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - orders:write removed, products:delete added
    await Assert.That(result.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(result.Permissions).DoesNotContain(Permission.Write("orders"));
    await Assert.That(result.Permissions).Contains(Permission.Delete("products"));
  }

  [Test]
  public async Task ApplyTo_PrincipalsRemoveThenAdd_AppliedInCorrectOrderAsync() {
    // Arrange - Manually construct delta with both remove and add for principals
    var previous = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "t1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("u1"),
        SecurityPrincipalId.Group("old-team")
      },
      Claims = new Dictionary<string, string>()
    };

    var addPrincipals = JsonSerializer.SerializeToElement(new[] { "group:new-team" });
    var removePrincipals = JsonSerializer.SerializeToElement(new[] { "group:old-team" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Principals] = new CollectionChanges { Add = addPrincipals, Remove = removePrincipals }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.SecurityPrincipals).Contains(SecurityPrincipalId.User("u1"));
    await Assert.That(result.SecurityPrincipals).DoesNotContain(SecurityPrincipalId.Group("old-team"));
    await Assert.That(result.SecurityPrincipals).Contains(SecurityPrincipalId.Group("new-team"));
  }

  #endregion

  #region ContextType Round-Trip

  [Test]
  [Arguments(SecurityContextType.User)]
  [Arguments(SecurityContextType.System)]
  [Arguments(SecurityContextType.Impersonated)]
  public async Task CreateDelta_ApplyTo_ContextType_RoundTripsAsync(SecurityContextType contextType) {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "t1" };
    var previous = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ContextType = SecurityContextType.User
    };
    var current = new ScopeContext {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ContextType = contextType
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    if (contextType == SecurityContextType.User) {
      // Same as previous, no delta
      await Assert.That(delta).IsNull();
      return;
    }

    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.ContextType).IsEqualTo(contextType);
  }

  #endregion

  #region _scopesEqual - Edge Cases

  [Test]
  public async Task CreateDelta_PreviousScopeNull_CurrentScopeSet_CapturesDeltaAsync() {
    // Arrange - Previous IScopeContext is null, current has scope
    var current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "t1", UserId = "u1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_ScopesEqualWithAllowedPrincipals_NoDeltaAsync() {
    // Arrange - Both scopes identical including AllowedPrincipals
    var scope1 = new PerspectiveScope {
      TenantId = "t1",
      UserId = "u1",
      CustomerId = "c1",
      OrganizationId = "o1",
      AllowedPrincipals = ["user:alice", "group:sales"]
    };
    var scope2 = new PerspectiveScope {
      TenantId = "t1",
      UserId = "u1",
      CustomerId = "c1",
      OrganizationId = "o1",
      AllowedPrincipals = ["user:alice", "group:sales"]
    };
    var previous = _createScopeContextFromScope(scope1);
    var current = _createScopeContextFromScope(scope2);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Scopes are equal, no delta
    await Assert.That(delta).IsNull();
  }

  [Test]
  public async Task CreateDelta_ScopesDifferOnlyInAllowedPrincipals_CreatesDeltaAsync() {
    // Arrange
    var scope1 = new PerspectiveScope {
      TenantId = "t1",
      AllowedPrincipals = ["user:alice"]
    };
    var scope2 = new PerspectiveScope {
      TenantId = "t1",
      AllowedPrincipals = ["user:alice", "user:bob"]
    };
    var previous = _createScopeContextFromScope(scope1);
    var current = _createScopeContextFromScope(scope2);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - AllowedPrincipals differ, so scope delta created
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
  }

  #endregion

  #region ScopeDelta.HasChanges

  [Test]
  public async Task ScopeDelta_HasChanges_WithOnlyValues_ReturnsTrueAsync() {
    // Arrange
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = JsonSerializer.SerializeToElement(new { t = "t1" })
      }
    };

    // Assert
    await Assert.That(delta.HasChanges).IsTrue();
  }

  [Test]
  public async Task ScopeDelta_HasChanges_WithOnlyCollections_ReturnsTrueAsync() {
    // Arrange
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges {
          Set = JsonSerializer.SerializeToElement(new[] { "Admin" })
        }
      }
    };

    // Assert
    await Assert.That(delta.HasChanges).IsTrue();
  }

  [Test]
  public async Task ScopeDelta_HasChanges_NullBoth_ReturnsFalseAsync() {
    // Arrange
    var delta = new ScopeDelta();

    // Assert
    await Assert.That(delta.HasChanges).IsFalse();
  }

  #endregion

  #region FromSecurityContext - Additional Coverage

  [Test]
  public async Task FromSecurityContext_WithEmptyTenantNonEmptyUser_ReturnsDeltaAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext {
      TenantId = "",
      UserId = "user-123"
    };

    // Act
    var result = ScopeDelta.FromSecurityContext(context);

    // Assert - UserId is non-empty, so delta should be created
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.HasChanges).IsTrue();
  }

  [Test]
  public async Task FromSecurityContext_WithNonEmptyTenantEmptyUser_ReturnsDeltaAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext {
      TenantId = "tenant-abc",
      UserId = ""
    };

    // Act
    var result = ScopeDelta.FromSecurityContext(context);

    // Assert
    await Assert.That(result).IsNotNull();

    // Verify ApplyTo works
    var applied = result!.ApplyTo(null);
    await Assert.That(applied.Scope.TenantId).IsEqualTo("tenant-abc");
  }

  [Test]
  public async Task FromSecurityContext_ApplyTo_NullValues_HandledAsync() {
    // Arrange - Both values null but not empty string
    var context = new Whizbang.Core.Observability.SecurityContext {
      TenantId = "t1",
      UserId = null
    };

    // Act
    var delta = ScopeDelta.FromSecurityContext(context);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(applied.Scope.UserId).IsNull();
  }

  #endregion

  #region Multi-hop Accumulation via ApplyTo

  [Test]
  public async Task ApplyTo_ThreeHops_AccumulatesCorrectlyAsync() {
    // Arrange - Simulate 3 hops with progressive changes
    // Hop 1: Initial scope
    var hop1Scope = new PerspectiveScope { TenantId = "t1", UserId = "u1" };
    var hop1 = new ScopeContext {
      Scope = hop1Scope,
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("u1") },
      Claims = new Dictionary<string, string> { ["sub"] = "u1" },
      ActualPrincipal = "u1@test.com",
      EffectivePrincipal = "u1@test.com",
      ContextType = SecurityContextType.User
    };

    var delta1 = ScopeDelta.CreateDelta(null, hop1);
    var after1 = delta1!.ApplyTo(null);

    // Hop 2: Add Admin role, add write permission
    var hop2 = new ScopeContext {
      Scope = hop1Scope,
      Roles = new HashSet<string> { "User", "Admin" },
      Permissions = new HashSet<Permission> { Permission.Read("orders"), Permission.Write("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("u1") },
      Claims = new Dictionary<string, string> { ["sub"] = "u1" },
      ActualPrincipal = "u1@test.com",
      EffectivePrincipal = "u1@test.com",
      ContextType = SecurityContextType.User
    };

    var delta2 = ScopeDelta.CreateDelta(hop1, hop2);
    var after2 = delta2!.ApplyTo(after1);

    // Hop 3: Impersonation - change effective principal and context type
    var hop3 = new ScopeContext {
      Scope = hop1Scope,
      Roles = new HashSet<string> { "User", "Admin" },
      Permissions = new HashSet<Permission> { Permission.Read("orders"), Permission.Write("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User("u1") },
      Claims = new Dictionary<string, string> { ["sub"] = "u1" },
      ActualPrincipal = "u1@test.com",
      EffectivePrincipal = "target@test.com",
      ContextType = SecurityContextType.Impersonated
    };

    var delta3 = ScopeDelta.CreateDelta(hop2, hop3);
    var after3 = delta3!.ApplyTo(after2);

    // Assert - Final state
    await Assert.That(after3.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(after3.Roles).Contains("User");
    await Assert.That(after3.Roles).Contains("Admin");
    await Assert.That(after3.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(after3.Permissions).Contains(Permission.Write("orders"));
    await Assert.That(after3.ActualPrincipal).IsEqualTo("u1@test.com");
    await Assert.That(after3.EffectivePrincipal).IsEqualTo("target@test.com");
    await Assert.That(after3.ContextType).IsEqualTo(SecurityContextType.Impersonated);
  }

  #endregion

  #region _serializeScope - Multiple AllowedPrincipals Serialization

  [Test]
  public async Task CreateDelta_ApplyTo_MultipleAllowedPrincipals_PreservesOrderAsync() {
    // Arrange - Tests the AllowedPrincipals serialization loop (i > 0 comma path)
    var scope = new PerspectiveScope {
      AllowedPrincipals = ["user:alice", "group:team-a", "group:team-b", "user:bob"]
    };
    var current = _createScopeContextFromScope(scope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(4);
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("user:alice");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("group:team-a");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("group:team-b");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("user:bob");
  }

  #endregion

  #region _serializeScope - Combined Properties (first=false paths)

  [Test]
  public async Task CreateDelta_ApplyTo_ScopeWithTenantAndCustomerOnly_PreservedAsync() {
    // Arrange - Tests first=false path for CustomerId when TenantId is set
    var scope = new PerspectiveScope {
      TenantId = "t1",
      CustomerId = "c1"
    };
    var current = _createScopeContextFromScope(scope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("c1");
    await Assert.That(applied.Scope.UserId).IsNull();
  }

  [Test]
  public async Task CreateDelta_ApplyTo_ScopeWithUserAndOrgOnly_PreservedAsync() {
    // Arrange - Tests first=false path for OrganizationId when UserId is set
    var scope = new PerspectiveScope {
      UserId = "u1",
      OrganizationId = "o1"
    };
    var current = _createScopeContextFromScope(scope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.UserId).IsEqualTo("u1");
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("o1");
    await Assert.That(applied.Scope.TenantId).IsNull();
  }

  [Test]
  public async Task CreateDelta_ApplyTo_ScopeWithAllFieldsAndPrincipals_PreservedAsync() {
    // Arrange - All 5 scope fields populated (exercises all comma insertion paths)
    var scope = new PerspectiveScope {
      TenantId = "t1",
      UserId = "u1",
      CustomerId = "c1",
      OrganizationId = "o1",
      AllowedPrincipals = ["principal-1"]
    };
    var current = _createScopeContextFromScope(scope);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(applied.Scope.UserId).IsEqualTo("u1");
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("c1");
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("o1");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("principal-1");
  }

  #endregion

  #region FromPerspectiveScope Null Return Coverage

  [Test]
  public async Task FromPerspectiveScope_NullScope_ReturnsNullAsync() {
    // Arrange & Act - null scope returns null (line 133)
    var result = ScopeDelta.FromPerspectiveScope(null);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromPerspectiveScope_AllEmptyFields_ReturnsNullAsync() {
    // Arrange - Scope with all null/empty fields returns null (line 138)
    var scope = new PerspectiveScope {
      TenantId = null,
      UserId = null,
      CustomerId = null,
      OrganizationId = null
    };

    // Act
    var result = ScopeDelta.FromPerspectiveScope(scope);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromPerspectiveScope_WithPopulatedFields_ReturnsDeltaAsync() {
    // Arrange - Scope with populated fields returns a delta
    var scope = new PerspectiveScope {
      TenantId = "tenant-1",
      UserId = "user-1"
    };

    // Act
    var result = ScopeDelta.FromPerspectiveScope(scope);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.HasChanges).IsTrue();
  }

  #endregion

  #region Test Helpers

  private static ScopeContext _createScopeContextFromScope(PerspectiveScope scope) =>
    new() {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

  #endregion
}
