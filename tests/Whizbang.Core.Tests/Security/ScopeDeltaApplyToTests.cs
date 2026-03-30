using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

// Suppress CA1861 for test file - constant array arguments are acceptable in test code
#pragma warning disable CA1861

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for ScopeDelta.ApplyTo and ScopeDelta.CreateDelta targeting uncovered code paths:
/// - ApplyTo with null previous (defaults)
/// - ApplyTo with all value types (Scope, Actual, Effective, Type)
/// - ApplyTo with collection changes (Set, Add, Remove for Roles/Perms/Principals/Claims)
/// - CreateDelta with partial collection changes (add/remove not full replacement)
/// - Full scope serialization with all fields (CustomerId, OrganizationId, AllowedPrincipals)
/// - Round-trip CreateDelta + ApplyTo for complex scenarios
/// </summary>
[Category("Security")]
[Category("ScopeDelta")]
public class ScopeDeltaApplyToTests {

  #region ApplyTo - Null Previous (Defaults)

  [Test]
  public async Task ApplyTo_NullPrevious_UsesDefaultsAsync() {
    // Arrange - delta with scope only
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = _parseJson("""{"t":"tenant1","u":"user1"}""")
      }
    };

    // Act
    var result = delta.ApplyTo(null);

    // Assert - should have scope applied and empty collections
    await Assert.That(result.Scope.TenantId).IsEqualTo("tenant1");
    await Assert.That(result.Scope.UserId).IsEqualTo("user1");
    await Assert.That(result.Roles.Count).IsEqualTo(0);
    await Assert.That(result.Permissions.Count).IsEqualTo(0);
    await Assert.That(result.SecurityPrincipals.Count).IsEqualTo(0);
    await Assert.That(result.Claims.Count).IsEqualTo(0);
    await Assert.That(result.ActualPrincipal).IsNull();
    await Assert.That(result.EffectivePrincipal).IsNull();
    await Assert.That(result.ContextType).IsEqualTo(SecurityContextType.User);
  }

  [Test]
  public async Task ApplyTo_NullPrevious_WithAllValues_SetsAllAsync() {
    // Arrange
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = _parseJson("""{"t":"t1","u":"u1","c":"c1","o":"o1","ap":["p1","p2"]}"""),
        [ScopeProp.Actual] = _parseJson("\"actual-user\""),
        [ScopeProp.Effective] = _parseJson("\"effective-user\""),
        [ScopeProp.Type] = _parseJson("2") // Impersonated = 2
      }
    };

    // Act
    var result = delta.ApplyTo(null);

    // Assert
    await Assert.That(result.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(result.Scope.UserId).IsEqualTo("u1");
    await Assert.That(result.Scope.CustomerId).IsEqualTo("c1");
    await Assert.That(result.Scope.OrganizationId).IsEqualTo("o1");
    await Assert.That(result.Scope.AllowedPrincipals.Count).IsEqualTo(2);
    await Assert.That(result.ActualPrincipal).IsEqualTo("actual-user");
    await Assert.That(result.EffectivePrincipal).IsEqualTo("effective-user");
    await Assert.That(result.ContextType).IsEqualTo(SecurityContextType.Impersonated);
  }

  #endregion

  #region ApplyTo - Collection Set Operations

  [Test]
  public async Task ApplyTo_RolesSet_ReplacesEntireCollectionAsync() {
    // Arrange - previous has roles, delta sets new ones
    var previous = _createScopeContext(roles: ["admin", "viewer"]);
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges {
          Set = _parseJson("""["editor","moderator"]""")
        }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Set replaces all
    await Assert.That(result.Roles.Count).IsEqualTo(2);
    await Assert.That(result.Roles.Contains("editor")).IsTrue();
    await Assert.That(result.Roles.Contains("moderator")).IsTrue();
    await Assert.That(result.Roles.Contains("admin")).IsFalse();
  }

  [Test]
  public async Task ApplyTo_PermissionsSet_ReplacesCollectionAsync() {
    // Arrange
    var previous = _createScopeContext(permissions: ["read", "write"]);
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Perms] = new CollectionChanges {
          Set = _parseJson("""["execute","delete"]""")
        }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.Permissions.Count).IsEqualTo(2);
    await Assert.That(result.Permissions.Contains(new Permission("execute"))).IsTrue();
    await Assert.That(result.Permissions.Contains(new Permission("delete"))).IsTrue();
  }

  [Test]
  public async Task ApplyTo_PrincipalsSet_ReplacesCollectionAsync() {
    // Arrange
    var previous = _createScopeContext(principals: ["group1"]);
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Principals] = new CollectionChanges {
          Set = _parseJson("""["group2","group3"]""")
        }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.SecurityPrincipals.Count).IsEqualTo(2);
    await Assert.That(result.SecurityPrincipals.Contains(new SecurityPrincipalId("group2"))).IsTrue();
    await Assert.That(result.SecurityPrincipals.Contains(new SecurityPrincipalId("group3"))).IsTrue();
  }

  #endregion

  #region ApplyTo - Collection Add/Remove Operations

  [Test]
  public async Task ApplyTo_RolesAddRemove_AppliesIncrementallyAsync() {
    // Arrange
    var previous = _createScopeContext(roles: ["admin", "viewer", "editor"]);
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges {
          Remove = _parseJson("""["viewer"]"""),
          Add = _parseJson("""["moderator"]""")
        }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Remove applied first, then Add
    await Assert.That(result.Roles.Count).IsEqualTo(3);
    await Assert.That(result.Roles.Contains("admin")).IsTrue();
    await Assert.That(result.Roles.Contains("editor")).IsTrue();
    await Assert.That(result.Roles.Contains("moderator")).IsTrue();
    await Assert.That(result.Roles.Contains("viewer")).IsFalse();
  }

  [Test]
  public async Task ApplyTo_ClaimsAddRemove_AppliesIncrementallyAsync() {
    // Arrange
    var previous = _createScopeContext(claims: new Dictionary<string, string> {
      ["key1"] = "value1",
      ["key2"] = "value2",
      ["key3"] = "value3"
    });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Claims] = new CollectionChanges {
          Remove = _parseJson("""["key2"]"""),
          Add = _parseJson("""{"key4":"value4","key1":"updated1"}""")
        }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.Claims.Count).IsEqualTo(3);
    await Assert.That(result.Claims["key1"]).IsEqualTo("updated1");
    await Assert.That(result.Claims["key3"]).IsEqualTo("value3");
    await Assert.That(result.Claims["key4"]).IsEqualTo("value4");
    await Assert.That(result.Claims.ContainsKey("key2")).IsFalse();
  }

  #endregion

  #region CreateDelta - Partial Changes

  [Test]
  public async Task CreateDelta_PartialRolesChange_UsesAddRemoveNotSetAsync() {
    // Arrange - previous has 3 roles, current removes 1 and adds 1
    var previous = _createScopeContext(roles: ["admin", "viewer", "editor"]);
    var current = _createScopeContext(roles: ["admin", "editor", "moderator"]);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - should use Add/Remove, not Set
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Roles)).IsTrue();
    var rolesChanges = delta.Collections[ScopeProp.Roles];
    await Assert.That(rolesChanges.Set.HasValue).IsFalse()
      .Because("Partial change should use Add/Remove not Set");
    await Assert.That(rolesChanges.Add.HasValue).IsTrue();
    await Assert.That(rolesChanges.Remove.HasValue).IsTrue();
  }

  [Test]
  public async Task CreateDelta_NoChanges_ReturnsNullAsync() {
    // Arrange
    var ctx = _createScopeContext(
      roles: ["admin"],
      permissions: ["read"],
      principals: ["group1"],
      claims: new Dictionary<string, string> { ["k"] = "v" });

    // Act
    var delta = ScopeDelta.CreateDelta(ctx, ctx);

    // Assert
    await Assert.That(delta).IsNull()
      .Because("No changes should return null");
  }

  [Test]
  public async Task CreateDelta_NullCurrent_ThrowsAsync() {
    // Act & Assert
    await Assert.That(() => ScopeDelta.CreateDelta(null, null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CreateDelta_ContextTypeChange_IncludesTypeValueAsync() {
    // Arrange
    var previous = _createScopeContext();
    var current = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ContextType = SecurityContextType.ServiceAccount
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Type)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_ActualEffectivePrincipalChange_IncludedAsync() {
    // Arrange
    var previous = _createScopeContext();
    var current = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ActualPrincipal = "user-actual",
      EffectivePrincipal = "user-effective"
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values!.ContainsKey(ScopeProp.Actual)).IsTrue();
    await Assert.That(delta.Values.ContainsKey(ScopeProp.Effective)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_ClaimsModifiedAndRemoved_TrackedAsync() {
    // Arrange
    var previous = _createScopeContext(claims: new Dictionary<string, string> {
      ["key1"] = "v1",
      ["key2"] = "v2",
      ["key3"] = "v3"
    });
    var current = _createScopeContext(claims: new Dictionary<string, string> {
      ["key1"] = "v1-modified",
      ["key3"] = "v3"
      // key2 removed
    });

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections!.ContainsKey(ScopeProp.Claims)).IsTrue();
    var claimsChanges = delta.Collections[ScopeProp.Claims];
    await Assert.That(claimsChanges.Add.HasValue).IsTrue()
      .Because("Modified key1 should be in Add");
    await Assert.That(claimsChanges.Remove.HasValue).IsTrue()
      .Because("Removed key2 should be in Remove");
  }

  #endregion

  #region CreateDelta + ApplyTo Round Trip

  [Test]
  public async Task CreateDelta_ApplyTo_RoundTrip_WithAllFieldsAsync() {
    // Arrange
    var previous = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "t1",
        UserId = "u1"
      },
      Roles = new HashSet<string>(["admin"]),
      Permissions = new HashSet<Permission>([new Permission("read")]),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>([new SecurityPrincipalId("g1")]),
      Claims = new Dictionary<string, string> { ["iss"] = "auth0" },
      ActualPrincipal = "user1",
      EffectivePrincipal = "user1",
      ContextType = SecurityContextType.User
    };

    var current = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "t2",
        UserId = "u2",
        CustomerId = "c1",
        OrganizationId = "o1"
      },
      Roles = new HashSet<string>(["editor", "viewer"]),
      Permissions = new HashSet<Permission>([new Permission("read"), new Permission("write")]),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>([new SecurityPrincipalId("g2")]),
      Claims = new Dictionary<string, string> { ["iss"] = "auth0", ["sub"] = "xyz" },
      ActualPrincipal = "admin1",
      EffectivePrincipal = "user2",
      ContextType = SecurityContextType.Impersonated
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    await Assert.That(delta).IsNotNull();
    var applied = delta!.ApplyTo(previous);

    // Assert - round trip should reproduce current
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t2");
    await Assert.That(applied.Scope.UserId).IsEqualTo("u2");
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("c1");
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("o1");
    await Assert.That(applied.ActualPrincipal).IsEqualTo("admin1");
    await Assert.That(applied.EffectivePrincipal).IsEqualTo("user2");
    await Assert.That(applied.ContextType).IsEqualTo(SecurityContextType.Impersonated);
    await Assert.That(applied.Claims["iss"]).IsEqualTo("auth0");
    await Assert.That(applied.Claims["sub"]).IsEqualTo("xyz");
  }

  [Test]
  public async Task CreateDelta_ApplyTo_RoundTrip_ScopeWithAllowedPrincipalsAsync() {
    // Arrange
    var previous = _createScopeContext();
    var current = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "t1",
        AllowedPrincipals = ["p1", "p2", "p3"]
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    await Assert.That(delta).IsNotNull();
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t1");
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(3);
    await Assert.That(applied.Scope.AllowedPrincipals[0]).IsEqualTo("p1");
    await Assert.That(applied.Scope.AllowedPrincipals[1]).IsEqualTo("p2");
    await Assert.That(applied.Scope.AllowedPrincipals[2]).IsEqualTo("p3");
  }

  #endregion

  #region ApplyTo - Null Values in Deserialization

  [Test]
  public async Task ApplyTo_NullActualPrincipal_DeserializesAsNullAsync() {
    // Arrange
    var previous = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      ActualPrincipal = "user1"
    };
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Actual] = _parseJson("null"),
        [ScopeProp.Effective] = _parseJson("null")
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.ActualPrincipal).IsNull();
    await Assert.That(result.EffectivePrincipal).IsNull();
  }

  #endregion

  #region FromSecurityContext

  [Test]
  public async Task FromSecurityContext_Null_ReturnsNullAsync() {
    var result = ScopeDelta.FromSecurityContext(null);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromSecurityContext_EmptyTenantAndUser_ReturnsNullAsync() {
    var ctx = new SecurityContext { TenantId = null, UserId = null };
    var result = ScopeDelta.FromSecurityContext(ctx);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromSecurityContext_WithTenantId_ReturnsDeltaAsync() {
    var ctx = new SecurityContext { TenantId = "t1", UserId = "u1" };
    var result = ScopeDelta.FromSecurityContext(ctx);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.HasChanges).IsTrue();
    await Assert.That(result.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
  }

  #endregion

  #region HasChanges

  [Test]
  public async Task HasChanges_EmptyDelta_ReturnsFalseAsync() {
    var delta = new ScopeDelta();
    await Assert.That(delta.HasChanges).IsFalse();
  }

  [Test]
  public async Task HasChanges_WithValues_ReturnsTrueAsync() {
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Actual] = _parseJson("\"user\"")
      }
    };
    await Assert.That(delta.HasChanges).IsTrue();
  }

  [Test]
  public async Task HasChanges_WithCollections_ReturnsTrueAsync() {
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges {
          Set = _parseJson("""["admin"]""")
        }
      }
    };
    await Assert.That(delta.HasChanges).IsTrue();
  }

  #endregion

  #region CollectionChanges.HasChanges

  [Test]
  public async Task CollectionChanges_Default_HasChangesFalseAsync() {
    var changes = new CollectionChanges();
    await Assert.That(changes.HasChanges).IsFalse();
  }

  [Test]
  public async Task CollectionChanges_WithSet_HasChangesTrueAsync() {
    var changes = new CollectionChanges {
      Set = _parseJson("""["a"]""")
    };
    await Assert.That(changes.HasChanges).IsTrue();
  }

  [Test]
  public async Task CollectionChanges_WithAdd_HasChangesTrueAsync() {
    var changes = new CollectionChanges {
      Add = _parseJson("""["a"]""")
    };
    await Assert.That(changes.HasChanges).IsTrue();
  }

  [Test]
  public async Task CollectionChanges_WithRemove_HasChangesTrueAsync() {
    var changes = new CollectionChanges {
      Remove = _parseJson("""["a"]""")
    };
    await Assert.That(changes.HasChanges).IsTrue();
  }

  #endregion

  #region Helpers

  private static JsonElement _parseJson(string json) {
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.Clone();
  }

  private static ScopeContext _createScopeContext(
      string[]? roles = null,
      string[]? permissions = null,
      string[]? principals = null,
      Dictionary<string, string>? claims = null) {
    return new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(roles ?? []),
      Permissions = new HashSet<Permission>((permissions ?? []).Select(p => new Permission(p))),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>((principals ?? []).Select(p => new SecurityPrincipalId(p))),
      Claims = claims ?? []
    };
  }

  #endregion
}
