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
/// Tests for ScopeDelta, CollectionChanges, and ScopeProp types.
/// These types enable delta-based scope propagation on message hops.
/// </summary>
/// <tests>Whizbang.Core/Security/ScopeDelta.cs</tests>
[Category("Security")]
[Category("ScopeDelta")]
public class ScopeDeltaTests {
  #region ScopeProp Enum Tests

  [Test]
  public async Task ScopeProp_HasExpectedValuesAsync() {
    // Assert - All expected enum values exist
    // Use variables to avoid TUnitAssertions0005 (constant value assertions)
    byte scopeValue = (byte)ScopeProp.Scope;
    byte rolesValue = (byte)ScopeProp.Roles;
    byte permsValue = (byte)ScopeProp.Perms;
    byte principalsValue = (byte)ScopeProp.Principals;
    byte claimsValue = (byte)ScopeProp.Claims;
    byte actualValue = (byte)ScopeProp.Actual;
    byte effectiveValue = (byte)ScopeProp.Effective;
    byte typeValue = (byte)ScopeProp.Type;

    await Assert.That(scopeValue).IsEqualTo((byte)0);
    await Assert.That(rolesValue).IsEqualTo((byte)1);
    await Assert.That(permsValue).IsEqualTo((byte)2);
    await Assert.That(principalsValue).IsEqualTo((byte)3);
    await Assert.That(claimsValue).IsEqualTo((byte)4);
    await Assert.That(actualValue).IsEqualTo((byte)5);
    await Assert.That(effectiveValue).IsEqualTo((byte)6);
    await Assert.That(typeValue).IsEqualTo((byte)7);
  }

  #endregion

  #region CollectionChanges Tests

  [Test]
  public async Task CollectionChanges_Default_HasNoChangesAsync() {
    // Arrange
    var changes = new CollectionChanges();

    // Assert
    await Assert.That(changes.HasChanges).IsFalse();
    await Assert.That(changes.Set).IsNull();
    await Assert.That(changes.Add).IsNull();
    await Assert.That(changes.Remove).IsNull();
  }

  [Test]
  public async Task CollectionChanges_WithSet_HasChangesAsync() {
    // Arrange
    var setElement = JsonSerializer.SerializeToElement(new[] { "Admin", "User" });
    var changes = new CollectionChanges { Set = setElement };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
    await Assert.That(changes.Set).IsNotNull();
  }

  [Test]
  public async Task CollectionChanges_WithAdd_HasChangesAsync() {
    // Arrange
    var addElement = JsonSerializer.SerializeToElement(new[] { "NewRole" });
    var changes = new CollectionChanges { Add = addElement };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
    await Assert.That(changes.Add).IsNotNull();
  }

  [Test]
  public async Task CollectionChanges_WithRemove_HasChangesAsync() {
    // Arrange
    var removeElement = JsonSerializer.SerializeToElement(new[] { "OldRole" });
    var changes = new CollectionChanges { Remove = removeElement };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
    await Assert.That(changes.Remove).IsNotNull();
  }

  [Test]
  public async Task CollectionChanges_WithAddAndRemove_HasChangesAsync() {
    // Arrange - Multiple operations
    var addElement = JsonSerializer.SerializeToElement(new[] { "Admin", "Manager" });
    var removeElement = JsonSerializer.SerializeToElement(new[] { "Guest", "Temp" });
    var changes = new CollectionChanges { Add = addElement, Remove = removeElement };

    // Assert
    await Assert.That(changes.HasChanges).IsTrue();
    await Assert.That(changes.Add).IsNotNull();
    await Assert.That(changes.Remove).IsNotNull();
    await Assert.That(changes.Set).IsNull();
  }

  [Test]
  public async Task CollectionChanges_Serialization_UsesShortPropertyNamesAsync() {
    // Arrange
    var setElement = JsonSerializer.SerializeToElement(new[] { "Admin" });
    var changes = new CollectionChanges { Set = setElement };

    // Act
    var json = JsonSerializer.Serialize(changes);

    // Assert - Uses "s" not "Set"
    await Assert.That(json).Contains("\"s\":");
    await Assert.That(json).DoesNotContain("\"Set\"");
  }

  [Test]
  public async Task CollectionChanges_Serialization_OmitsNullValuesAsync() {
    // Arrange - Only Add is set
    var addElement = JsonSerializer.SerializeToElement(new[] { "NewRole" });
    var changes = new CollectionChanges { Add = addElement };

    // Act
    var json = JsonSerializer.Serialize(changes);

    // Assert - "s" and "r" should not appear
    await Assert.That(json).Contains("\"a\":");
    await Assert.That(json).DoesNotContain("\"s\":");
    await Assert.That(json).DoesNotContain("\"r\":");
  }

  [Test]
  public async Task CollectionChanges_Deserialization_RoundTripsCorrectlyAsync() {
    // Arrange
    var addElement = JsonSerializer.SerializeToElement(new[] { "Admin", "Manager" });
    var removeElement = JsonSerializer.SerializeToElement(new[] { "Guest" });
    var original = new CollectionChanges { Add = addElement, Remove = removeElement };

    // Act
    var json = JsonSerializer.Serialize(original);
    var deserialized = JsonSerializer.Deserialize<CollectionChanges>(json);

    // Assert
    await Assert.That(deserialized.HasChanges).IsTrue();
    await Assert.That(deserialized.Add).IsNotNull();
    await Assert.That(deserialized.Remove).IsNotNull();
    await Assert.That(deserialized.Set).IsNull();
  }

  #endregion

  #region ScopeDelta Creation Tests

  [Test]
  public async Task ScopeDelta_Default_HasNoChangesAsync() {
    // Arrange
    var delta = new ScopeDelta();

    // Assert
    await Assert.That(delta.HasChanges).IsFalse();
    await Assert.That(delta.Values).IsNull();
    await Assert.That(delta.Collections).IsNull();
  }

  [Test]
  public async Task ScopeDelta_WithValues_HasChangesAsync() {
    // Arrange
    var scopeElement = JsonSerializer.SerializeToElement(new { t = "tenant-123" });
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      }
    };

    // Assert
    await Assert.That(delta.HasChanges).IsTrue();
  }

  [Test]
  public async Task ScopeDelta_WithCollections_HasChangesAsync() {
    // Arrange
    var addRoles = JsonSerializer.SerializeToElement(new[] { "Admin" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Add = addRoles }
      }
    };

    // Assert
    await Assert.That(delta.HasChanges).IsTrue();
  }

  [Test]
  public async Task ScopeDelta_WithEmptyDictionaries_HasNoChangesAsync() {
    // Arrange - Empty dictionaries should not count as changes
    var delta = new ScopeDelta {
      Values = [],
      Collections = []
    };

    // Assert
    await Assert.That(delta.HasChanges).IsFalse();
  }

  #endregion

  #region CreateDelta Tests

  [Test]
  public async Task CreateDelta_WhenNothingChanged_ReturnsNullAsync() {
    // Arrange - Same scope context
    var scope = _createTestScope("tenant-1", "user-1", ["Admin"]);
    var previous = _createTestScopeContext(scope, ["Admin"], [], []);
    var current = _createTestScopeContext(scope, ["Admin"], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Nothing changed
    await Assert.That(delta).IsNull();
  }

  [Test]
  public async Task CreateDelta_WhenScopeChanged_ReturnsOnlyScopeChangesAsync() {
    // Arrange
    var previousScope = _createTestScope("tenant-1", "user-1", []);
    var currentScope = _createTestScope("tenant-2", "user-1", []); // TenantId changed
    var previous = _createTestScopeContext(previousScope, [], [], []);
    var current = _createTestScopeContext(currentScope, [], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(delta.Collections).IsNull();
  }

  [Test]
  public async Task CreateDelta_WhenRolesAdded_ReturnsAddOperationAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, ["User"], [], []);
    var current = _createTestScopeContext(scope, ["User", "Admin", "Manager"], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Roles)).IsTrue();
    var rolesChanges = delta.Collections[ScopeProp.Roles];
    await Assert.That(rolesChanges.Add).IsNotNull();
    await Assert.That(rolesChanges.Set).IsNull();
  }

  [Test]
  public async Task CreateDelta_WhenRolesRemoved_ReturnsRemoveOperationAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, ["User", "Admin", "Guest"], [], []);
    var current = _createTestScopeContext(scope, ["User"], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Roles)).IsTrue();
    var rolesChanges = delta.Collections[ScopeProp.Roles];
    await Assert.That(rolesChanges.Remove).IsNotNull();
  }

  [Test]
  public async Task CreateDelta_WhenRolesAddedAndRemoved_ReturnsBothOperationsAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, ["User", "Guest", "Temp"], [], []);
    var current = _createTestScopeContext(scope, ["User", "Admin", "Manager"], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    var rolesChanges = delta.Collections![ScopeProp.Roles];
    await Assert.That(rolesChanges.Add).IsNotNull();
    await Assert.That(rolesChanges.Remove).IsNotNull();
    await Assert.That(rolesChanges.Set).IsNull();
  }

  [Test]
  public async Task CreateDelta_WhenPermissionsChanged_ReturnsPermissionChangesAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [Permission.Read("orders")], []);
    var current = _createTestScopeContext(scope, [], [Permission.Read("orders"), Permission.Write("orders")], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Perms)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_WhenPrincipalsChanged_ReturnsPrincipalsChangesAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [], [SecurityPrincipalId.User("user-1")]);
    var current = _createTestScopeContext(scope, [], [], [
      SecurityPrincipalId.User("user-1"),
      SecurityPrincipalId.Group("sales-team")
    ]);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Principals)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_WhenActualPrincipalChanged_ReturnsActualChangeAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [], [], actualPrincipal: "admin@example.com");
    var current = _createTestScopeContext(scope, [], [], [], actualPrincipal: "support@example.com");

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Actual)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_WhenEffectivePrincipalChanged_ReturnsEffectiveChangeAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [], [], effectivePrincipal: "user@example.com");
    var current = _createTestScopeContext(scope, [], [], [], effectivePrincipal: "impersonated@example.com");

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Effective)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_WhenContextTypeChanged_ReturnsTypeChangeAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [], [], contextType: SecurityContextType.User);
    var current = _createTestScopeContext(scope, [], [], [], contextType: SecurityContextType.Impersonated);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Type)).IsTrue();
  }

  [Test]
  public async Task CreateDelta_WithNullPrevious_ReturnsFullDeltaAsync() {
    // Arrange - No previous context
    var scope = _createTestScope("tenant-1", "user-1", []);
    var current = _createTestScopeContext(scope, ["Admin"], [Permission.Read("orders")], []);

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);

    // Assert - Full context captured
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.HasChanges).IsTrue();
    await Assert.That(delta.Values).IsNotNull();
    await Assert.That(delta.Collections).IsNotNull();
  }

  [Test]
  public async Task CreateDelta_ThrowsOnNullCurrentAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [], []);

    // Act & Assert
    await Assert.That(() => ScopeDelta.CreateDelta(previous, null!)).Throws<ArgumentNullException>();
  }

  #endregion

  #region ApplyTo Tests

  [Test]
  public async Task ApplyTo_WithNullPrevious_CreatesNewScopeAsync() {
    // Arrange
    var scopeElement = JsonSerializer.SerializeToElement(new { t = "tenant-123", u = "user-456" });
    var rolesElement = JsonSerializer.SerializeToElement(new[] { "Admin", "User" });
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      },
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Set = rolesElement }
      }
    };

    // Act
    var result = delta.ApplyTo(null);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Scope.TenantId).IsEqualTo("tenant-123");
    await Assert.That(result.Scope.UserId).IsEqualTo("user-456");
    await Assert.That(result.Roles).Contains("Admin");
    await Assert.That(result.Roles).Contains("User");
  }

  [Test]
  public async Task ApplyTo_WithSetOperation_ReplacesCollectionAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      ["OldRole1", "OldRole2"],
      [],
      []
    );
    var newRoles = JsonSerializer.SerializeToElement(new[] { "NewRole" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Set = newRoles }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Old roles gone, new role present
    await Assert.That(result.Roles.Count).IsEqualTo(1);
    await Assert.That(result.Roles).Contains("NewRole");
    await Assert.That(result.Roles).DoesNotContain("OldRole1");
  }

  [Test]
  public async Task ApplyTo_WithAddOperation_AddsToCollectionAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      ["ExistingRole"],
      [],
      []
    );
    var addRoles = JsonSerializer.SerializeToElement(new[] { "NewRole1", "NewRole2" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Add = addRoles }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Both old and new roles present
    await Assert.That(result.Roles.Count).IsEqualTo(3);
    await Assert.That(result.Roles).Contains("ExistingRole");
    await Assert.That(result.Roles).Contains("NewRole1");
    await Assert.That(result.Roles).Contains("NewRole2");
  }

  [Test]
  public async Task ApplyTo_WithRemoveOperation_RemovesFromCollectionAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      ["Role1", "Role2", "Role3"],
      [],
      []
    );
    var removeRoles = JsonSerializer.SerializeToElement(new[] { "Role2" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Remove = removeRoles }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Role2 removed
    await Assert.That(result.Roles.Count).IsEqualTo(2);
    await Assert.That(result.Roles).Contains("Role1");
    await Assert.That(result.Roles).Contains("Role3");
    await Assert.That(result.Roles).DoesNotContain("Role2");
  }

  [Test]
  public async Task ApplyTo_WithAddAndRemoveOperations_AppliesRemoveFirstThenAddAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      ["Role1", "Role2"],
      [],
      []
    );
    var addRoles = JsonSerializer.SerializeToElement(new[] { "Role3", "Role4" });
    var removeRoles = JsonSerializer.SerializeToElement(new[] { "Role1" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Add = addRoles, Remove = removeRoles }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Role1 removed, Role3+Role4 added
    await Assert.That(result.Roles.Count).IsEqualTo(3);
    await Assert.That(result.Roles).Contains("Role2");
    await Assert.That(result.Roles).Contains("Role3");
    await Assert.That(result.Roles).Contains("Role4");
    await Assert.That(result.Roles).DoesNotContain("Role1");
  }

  [Test]
  public async Task ApplyTo_SetTakesPrecedenceOverAddRemoveAsync() {
    // Arrange - Set + Add + Remove: Set should win
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      ["OldRole"],
      [],
      []
    );
    var setRoles = JsonSerializer.SerializeToElement(new[] { "SetRole" });
    var addRoles = JsonSerializer.SerializeToElement(new[] { "AddedRole" });
    var removeRoles = JsonSerializer.SerializeToElement(new[] { "OldRole" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Set = setRoles, Add = addRoles, Remove = removeRoles }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Only SetRole present (Set takes precedence)
    await Assert.That(result.Roles.Count).IsEqualTo(1);
    await Assert.That(result.Roles).Contains("SetRole");
  }

  [Test]
  public async Task ApplyTo_PreservesUnchangedPropertiesAsync() {
    // Arrange - Only roles change, scope unchanged
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", ["principal-1"]),
      ["OldRole"],
      [Permission.Read("orders")],
      [SecurityPrincipalId.User("user-1")]
    );
    var addRoles = JsonSerializer.SerializeToElement(new[] { "NewRole" });
    var delta = new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Add = addRoles }
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert - Unchanged properties preserved
    await Assert.That(result.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(result.Scope.UserId).IsEqualTo("user-1");
    await Assert.That(result.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(result.SecurityPrincipals).Contains(SecurityPrincipalId.User("user-1"));
  }

  [Test]
  public async Task ApplyTo_WithScopeValueChange_UpdatesScopeAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      [],
      [],
      []
    );
    var newScope = JsonSerializer.SerializeToElement(new { t = "tenant-2", u = "user-1" });
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = newScope
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.Scope.TenantId).IsEqualTo("tenant-2");
    await Assert.That(result.Scope.UserId).IsEqualTo("user-1");
  }

  [Test]
  public async Task ApplyTo_WithActualPrincipalChange_UpdatesActualAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      [],
      [],
      [],
      actualPrincipal: "old@example.com"
    );
    var newActual = JsonSerializer.SerializeToElement("new@example.com");
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Actual] = newActual
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.ActualPrincipal).IsEqualTo("new@example.com");
  }

  #endregion

  #region JSON Serialization Tests

  [Test]
  public async Task ScopeDelta_Serialization_UsesShortPropertyNamesAsync() {
    // Arrange
    var scopeElement = JsonSerializer.SerializeToElement(new { t = "tenant-1" });
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      }
    };

    // Act
    var json = JsonSerializer.Serialize(delta);

    // Assert - Uses "v" not "Values"
    await Assert.That(json).Contains("\"v\":");
    await Assert.That(json).DoesNotContain("\"Values\"");
  }

  [Test]
  public async Task ScopeDelta_Serialization_OmitsNullValuesAsync() {
    // Arrange - Only Values, no Collections
    var scopeElement = JsonSerializer.SerializeToElement(new { t = "tenant-1" });
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      }
    };

    // Act
    var json = JsonSerializer.Serialize(delta);

    // Assert - "c" (Collections) should not appear
    await Assert.That(json).Contains("\"v\":");
    await Assert.That(json).DoesNotContain("\"c\":");
  }

  [Test]
  public async Task ScopeDelta_Serialization_RoundTripsCorrectlyAsync() {
    // Arrange
    var scopeElement = JsonSerializer.SerializeToElement(new { t = "tenant-1", u = "user-1" });
    var rolesAdd = JsonSerializer.SerializeToElement(new[] { "Admin" });
    var rolesRemove = JsonSerializer.SerializeToElement(new[] { "Guest" });
    var original = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      },
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = new CollectionChanges { Add = rolesAdd, Remove = rolesRemove }
      }
    };

    // Act
    var json = JsonSerializer.Serialize(original);
    var deserialized = JsonSerializer.Deserialize<ScopeDelta>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.HasChanges).IsTrue();
    await Assert.That(deserialized.Values).IsNotNull();
    await Assert.That(deserialized.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(deserialized.Collections).IsNotNull();
    await Assert.That(deserialized.Collections!.ContainsKey(ScopeProp.Roles)).IsTrue();
    await Assert.That(deserialized.Collections[ScopeProp.Roles].Add).IsNotNull();
    await Assert.That(deserialized.Collections[ScopeProp.Roles].Remove).IsNotNull();
  }

  [Test]
  public async Task ScopeDelta_SerializesEnumKeysAsAbbreviatedStringsAsync() {
    // Arrange
    var scopeElement = JsonSerializer.SerializeToElement(new { t = "tenant-1" });
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      }
    };

    // Act
    var json = JsonSerializer.Serialize(delta);

    // Assert - Enum keys are serialized as 2-character abbreviated strings
    // via ScopePropJsonConverter (Scope -> "Sc")
    await Assert.That(json).Contains("\"Sc\":");
    await Assert.That(json).DoesNotContain("\"Scope\":");
  }

  #endregion

  #region Edge Cases

  [Test]
  public async Task CreateDelta_WithEmptyCollections_DoesNotIncludeThemAsync() {
    // Arrange - Both have same empty roles
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previous = _createTestScopeContext(scope, [], [], []);
    var current = _createTestScopeContext(scope, [], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - No delta needed
    await Assert.That(delta).IsNull();
  }

  [Test]
  public async Task ApplyTo_EmptyDelta_ReturnsPreviousUnchangedAsync() {
    // Arrange
    var previous = _createTestScopeContext(
      _createTestScope("tenant-1", "user-1", []),
      ["Admin"],
      [Permission.Read("orders")],
      []
    );
    var emptyDelta = new ScopeDelta();

    // Act
    var result = emptyDelta.ApplyTo(previous);

    // Assert - Same values as previous
    await Assert.That(result.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(result.Roles).Contains("Admin");
    await Assert.That(result.Permissions).Contains(Permission.Read("orders"));
  }

  [Test]
  public async Task CreateDelta_ClaimsChanges_CapturedInCollectionsAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1", []);
    var previousClaims = new Dictionary<string, string> { ["sub"] = "user-1" };
    var currentClaims = new Dictionary<string, string> { ["sub"] = "user-1", ["email"] = "user@example.com" };
    var previous = _createTestScopeContext(scope, [], [], [], claims: previousClaims);
    var current = _createTestScopeContext(scope, [], [], [], claims: currentClaims);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Claims)).IsTrue();
  }

  #endregion

  #region Test Helpers

  private static PerspectiveScope _createTestScope(string? tenantId, string? userId, string[] allowedPrincipals) =>
    new() {
      TenantId = tenantId,
      UserId = userId,
      AllowedPrincipals = [.. allowedPrincipals]
    };

  private static ScopeContext _createTestScopeContext(
      PerspectiveScope scope,
      string[] roles,
      Permission[] permissions,
      SecurityPrincipalId[] principals,
      string? actualPrincipal = null,
      string? effectivePrincipal = null,
      SecurityContextType contextType = SecurityContextType.User,
      Dictionary<string, string>? claims = null) =>
    new() {
      Scope = scope,
      Roles = roles.ToHashSet(),
      Permissions = permissions.ToHashSet(),
      SecurityPrincipals = principals.ToHashSet(),
      Claims = claims ?? new Dictionary<string, string>(),
      ActualPrincipal = actualPrincipal,
      EffectivePrincipal = effectivePrincipal,
      ContextType = contextType
    };

  #endregion

  #region ScopePropJsonConverter Tests

  // Static options to avoid CA1869 - cache and reuse JsonSerializerOptions
  private static readonly JsonSerializerOptions _scopePropConverterOptions = new() {
    Converters = { new ScopePropJsonConverter() }
  };

  [Test]
  public async Task ScopePropJsonConverter_Serialize_UsesAbbreviatedNamesAsync() {
    // Arrange - Create a dictionary with ScopeProp keys
    var dict = new Dictionary<ScopeProp, string> {
      [ScopeProp.Scope] = "test-value"
    };

    // Act
    var json = JsonSerializer.Serialize(dict, _scopePropConverterOptions);

    // Assert - Should use "Sc" not "Scope"
    await Assert.That(json).Contains("\"Sc\"");
    await Assert.That(json).DoesNotContain("\"Scope\"");
  }

  [Test]
  public async Task ScopePropJsonConverter_Deserialize_ReadsAbbreviatedNamesAsync() {
    // Arrange - JSON with abbreviated key
    var json = """{"Sc":"test-value"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _scopePropConverterOptions);

    // Assert
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(dict[ScopeProp.Scope]).IsEqualTo("test-value");
  }

  [Test]
  [Arguments(ScopeProp.Scope, "Sc")]
  [Arguments(ScopeProp.Roles, "Ro")]
  [Arguments(ScopeProp.Perms, "Pe")]
  [Arguments(ScopeProp.Principals, "Pr")]
  [Arguments(ScopeProp.Claims, "Cl")]
  [Arguments(ScopeProp.Actual, "Ac")]
  [Arguments(ScopeProp.Effective, "Ef")]
  [Arguments(ScopeProp.Type, "Ty")]
  public async Task ScopePropJsonConverter_AllEnumValues_HaveCorrectAbbreviationsAsync(
      ScopeProp prop, string expectedAbbrev) {
    // Act - Serialize the enum value directly
    var json = JsonSerializer.Serialize(prop, _scopePropConverterOptions);

    // Assert
    await Assert.That(json).IsEqualTo($"\"{expectedAbbrev}\"");
  }

  [Test]
  public async Task ScopePropJsonConverter_RoundTrip_PreservesAllValuesAsync() {
    // Arrange - Dictionary with all ScopeProp values
    var original = new Dictionary<ScopeProp, string> {
      [ScopeProp.Scope] = "scope-val",
      [ScopeProp.Roles] = "roles-val",
      [ScopeProp.Perms] = "perms-val",
      [ScopeProp.Principals] = "principals-val",
      [ScopeProp.Claims] = "claims-val",
      [ScopeProp.Actual] = "actual-val",
      [ScopeProp.Effective] = "effective-val",
      [ScopeProp.Type] = "type-val"
    };

    // Act - Round-trip
    var json = JsonSerializer.Serialize(original, _scopePropConverterOptions);
    var deserialized = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _scopePropConverterOptions);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Count).IsEqualTo(8);
    foreach (var kvp in original) {
      await Assert.That(deserialized.ContainsKey(kvp.Key)).IsTrue();
      await Assert.That(deserialized[kvp.Key]).IsEqualTo(kvp.Value);
    }
  }

  [Test]
  public async Task ScopePropJsonConverter_DeserializeLegacyFullName_FallsBackToEnumParseAsync() {
    // Arrange - JSON with full enum name (legacy format)
    var json = """{"Scope":"test-value"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _scopePropConverterOptions);

    // Assert - Should still work via Enum.Parse fallback
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.ContainsKey(ScopeProp.Scope)).IsTrue();
  }

  #endregion

  #region FromSecurityContext Tests

  [Test]
  public async Task FromSecurityContext_WithNullInput_ReturnsNullAsync() {
    // Act
    var result = ScopeDelta.FromSecurityContext(null);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromSecurityContext_WithEmptyTenantAndUser_ReturnsNullAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext { TenantId = null, UserId = null };

    // Act
    var result = ScopeDelta.FromSecurityContext(context);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromSecurityContext_WithEmptyStringTenantAndUser_ReturnsNullAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext { TenantId = "", UserId = "" };

    // Act
    var result = ScopeDelta.FromSecurityContext(context);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FromSecurityContext_WithOnlyTenantId_ReturnsDeltaAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext { TenantId = "tenant-abc", UserId = null };

    // Act
    var result = ScopeDelta.FromSecurityContext(context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.HasChanges).IsTrue();
    await Assert.That(result.Values).IsNotNull();
    await Assert.That(result.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
  }

  [Test]
  public async Task FromSecurityContext_WithOnlyUserId_ReturnsDeltaAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext { TenantId = null, UserId = "user-xyz" };

    // Act
    var result = ScopeDelta.FromSecurityContext(context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.HasChanges).IsTrue();
  }

  [Test]
  public async Task FromSecurityContext_WithBothValues_AppliesCorrectlyAsync() {
    // Arrange
    var context = new Whizbang.Core.Observability.SecurityContext { TenantId = "tenant-1", UserId = "user-1" };

    // Act
    var delta = ScopeDelta.FromSecurityContext(context);
    var scope = delta!.ApplyTo(null);

    // Assert
    await Assert.That(scope.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(scope.Scope.UserId).IsEqualTo("user-1");
  }

  #endregion

  #region CreateDelta and ApplyTo Coverage - CustomerId, OrganizationId, AllowedPrincipals

  [Test]
  public async Task CreateDelta_WithCustomerIdChange_CapturesScopeChangeAsync() {
    // Arrange
    var previousScope = new PerspectiveScope { TenantId = "t1", CustomerId = "cust-1" };
    var currentScope = new PerspectiveScope { TenantId = "t1", CustomerId = "cust-2" };
    var previous = _createTestScopeContextFromScope(previousScope);
    var current = _createTestScopeContextFromScope(currentScope);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();

    // Verify round-trip through ApplyTo
    var applied = delta.ApplyTo(previous);
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("cust-2");
  }

  [Test]
  public async Task CreateDelta_WithOrganizationIdChange_CapturesScopeChangeAsync() {
    // Arrange
    var previousScope = new PerspectiveScope { TenantId = "t1", OrganizationId = "org-1" };
    var currentScope = new PerspectiveScope { TenantId = "t1", OrganizationId = "org-2" };
    var previous = _createTestScopeContextFromScope(previousScope);
    var current = _createTestScopeContextFromScope(currentScope);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();

    // Verify round-trip
    var applied = delta!.ApplyTo(previous);
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("org-2");
  }

  [Test]
  public async Task CreateDelta_WithAllowedPrincipalsChange_CapturesScopeChangeAsync() {
    // Arrange
    var previousScope = new PerspectiveScope {
      TenantId = "t1",
      AllowedPrincipals = ["user:alice"]
    };
    var currentScope = new PerspectiveScope {
      TenantId = "t1",
      AllowedPrincipals = ["user:alice", "group:sales"]
    };
    var previous = _createTestScopeContextFromScope(previousScope);
    var current = _createTestScopeContextFromScope(currentScope);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();

    // Verify round-trip
    var applied = delta!.ApplyTo(previous);
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(2);
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("user:alice");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("group:sales");
  }

  [Test]
  public async Task ApplyTo_WithFullScope_DeserializesAllFieldsAsync() {
    // Arrange - Create delta with all scope fields via CreateDelta
    var currentScope = new PerspectiveScope {
      TenantId = "t-1",
      UserId = "u-1",
      CustomerId = "c-1",
      OrganizationId = "o-1",
      AllowedPrincipals = ["user:bob", "group:dev"]
    };
    var current = _createTestScopeContextFromScope(currentScope);
    var delta = ScopeDelta.CreateDelta(null, current);

    // Act
    var applied = delta!.ApplyTo(null);

    // Assert
    await Assert.That(applied.Scope.TenantId).IsEqualTo("t-1");
    await Assert.That(applied.Scope.UserId).IsEqualTo("u-1");
    await Assert.That(applied.Scope.CustomerId).IsEqualTo("c-1");
    await Assert.That(applied.Scope.OrganizationId).IsEqualTo("o-1");
    await Assert.That(applied.Scope.AllowedPrincipals.Count).IsEqualTo(2);
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("user:bob");
    await Assert.That(applied.Scope.AllowedPrincipals).Contains("group:dev");
  }

  #endregion

  #region Claims Delta Tests

  [Test]
  public async Task CreateDelta_WithClaimsRemoved_CapturesRemovalAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previousClaims = new Dictionary<string, string> {
      ["sub"] = "user-1",
      ["email"] = "user@example.com",
      ["role"] = "admin"
    };
    var currentClaims = new Dictionary<string, string> {
      ["sub"] = "user-1"
    };
    var previous = _createTestScopeContext(scope, [], [], [], claims: previousClaims);
    var current = _createTestScopeContext(scope, [], [], [], claims: currentClaims);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Claims)).IsTrue();
    var claimsChanges = delta.Collections[ScopeProp.Claims];
    await Assert.That(claimsChanges.Remove).IsNotNull();
  }

  [Test]
  public async Task CreateDelta_WithClaimsModified_CapturesModificationAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previousClaims = new Dictionary<string, string> { ["role"] = "user" };
    var currentClaims = new Dictionary<string, string> { ["role"] = "admin" };
    var previous = _createTestScopeContext(scope, [], [], [], claims: previousClaims);
    var current = _createTestScopeContext(scope, [], [], [], claims: currentClaims);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    await Assert.That(delta.Collections!.ContainsKey(ScopeProp.Claims)).IsTrue();
    var claimsChanges = delta.Collections[ScopeProp.Claims];
    await Assert.That(claimsChanges.Add).IsNotNull();
  }

  [Test]
  public async Task ApplyTo_WithClaimsRemoveAndAdd_AppliesCorrectlyAsync() {
    // Arrange - Create a full round trip: create delta then apply
    var scope = _createTestScope("t1", "u1", []);
    var previousClaims = new Dictionary<string, string> {
      ["sub"] = "user-1",
      ["old-claim"] = "remove-me"
    };
    var currentClaims = new Dictionary<string, string> {
      ["sub"] = "user-1",
      ["new-claim"] = "added"
    };
    var previous = _createTestScopeContext(scope, [], [], [], claims: previousClaims);
    var current = _createTestScopeContext(scope, [], [], [], claims: currentClaims);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Claims["sub"]).IsEqualTo("user-1");
    await Assert.That(applied.Claims.ContainsKey("new-claim")).IsTrue();
    await Assert.That(applied.Claims["new-claim"]).IsEqualTo("added");
    await Assert.That(applied.Claims.ContainsKey("old-claim")).IsFalse();
  }

  [Test]
  public async Task CreateDelta_ClaimsUnchanged_ReturnsNoDeltaAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var claims = new Dictionary<string, string> { ["sub"] = "user-1" };
    var previous = _createTestScopeContext(scope, [], [], [], claims: claims);
    var current = _createTestScopeContext(scope, [], [], [], claims: new Dictionary<string, string>(claims));

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert
    await Assert.That(delta).IsNull();
  }

  #endregion

  #region ApplyTo - Permissions, Principals, EffectivePrincipal, ContextType

  [Test]
  public async Task ApplyTo_WithPermissionChanges_AppliesCorrectlyAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previous = _createTestScopeContext(scope, [], [Permission.Read("orders")], []);
    var current = _createTestScopeContext(scope, [],
      [Permission.Read("orders"), Permission.Write("orders"), Permission.Delete("products")], []);

    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(applied.Permissions).Contains(Permission.Write("orders"));
    await Assert.That(applied.Permissions).Contains(Permission.Delete("products"));
  }

  [Test]
  public async Task ApplyTo_WithPrincipalChanges_AppliesCorrectlyAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previous = _createTestScopeContext(scope, [], [], [SecurityPrincipalId.User("u1")]);
    var current = _createTestScopeContext(scope, [], [],
      [SecurityPrincipalId.User("u1"), SecurityPrincipalId.Group("dev-team")]);

    var delta = ScopeDelta.CreateDelta(previous, current);
    var applied = delta!.ApplyTo(previous);

    // Assert
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.User("u1"));
    await Assert.That(applied.SecurityPrincipals).Contains(SecurityPrincipalId.Group("dev-team"));
  }

  [Test]
  public async Task ApplyTo_WithEffectivePrincipalChange_UpdatesEffectiveAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previous = _createTestScopeContext(scope, [], [], [], effectivePrincipal: "original@test.com");
    var effectiveElement = JsonSerializer.SerializeToElement("impersonated@test.com");
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Effective] = effectiveElement
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.EffectivePrincipal).IsEqualTo("impersonated@test.com");
  }

  [Test]
  public async Task ApplyTo_WithContextTypeChange_UpdatesContextTypeAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previous = _createTestScopeContext(scope, [], [], [], contextType: SecurityContextType.User);
    var typeElement = JsonSerializer.SerializeToElement((int)SecurityContextType.Impersonated);
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Type] = typeElement
      }
    };

    // Act
    var result = delta.ApplyTo(previous);

    // Assert
    await Assert.That(result.ContextType).IsEqualTo(SecurityContextType.Impersonated);
  }

  [Test]
  public async Task ApplyTo_WithNullActualPrincipal_DeserializesNullAsync() {
    // Arrange - Test _deserializeString null path
    using var doc = JsonDocument.Parse("null");
    var nullElement = doc.RootElement.Clone();
    var delta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Actual] = nullElement
      }
    };

    // Act
    var result = delta.ApplyTo(null);

    // Assert
    await Assert.That(result.ActualPrincipal).IsNull();
  }

  #endregion

  #region CreateDelta - Collection Full Replacement Path

  [Test]
  public async Task CreateDelta_WhenAllRolesReplacedWithNewOnes_UsesSetOperationAsync() {
    // Arrange - All previous roles removed, all new roles added = full replacement
    var scope = _createTestScope("t1", "u1", []);
    var previous = _createTestScopeContext(scope, ["Admin", "Manager"], [], []);
    var current = _createTestScopeContext(scope, ["SuperAdmin", "Viewer"], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Should use Set operation for full replacement
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    var rolesChanges = delta.Collections![ScopeProp.Roles];
    await Assert.That(rolesChanges.Set).IsNotNull();
  }

  [Test]
  public async Task CreateDelta_WhenPreviousEmpty_CurrentHasValues_UsesSetOperationAsync() {
    // Arrange
    var scope = _createTestScope("t1", "u1", []);
    var previous = _createTestScopeContext(scope, [], [], []);
    var current = _createTestScopeContext(scope, ["Admin"], [], []);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - First-time collection should use Set
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNotNull();
    var rolesChanges = delta.Collections![ScopeProp.Roles];
    await Assert.That(rolesChanges.Set).IsNotNull();
  }

  #endregion

  #region CreateDelta - _scopesEqual edge cases

  [Test]
  public async Task CreateDelta_BothScopesNull_NoDeltaForScopeAsync() {
    // Arrange - Both have null scopes effectively (no tenant/user)
    var scope1 = new PerspectiveScope();
    var scope2 = new PerspectiveScope();
    var previous = _createTestScopeContextFromScope(scope1);
    var current = _createTestScopeContextFromScope(scope2);

    // Act
    var delta = ScopeDelta.CreateDelta(previous, current);

    // Assert - Nothing changed
    await Assert.That(delta).IsNull();
  }

  [Test]
  public async Task CreateDelta_PreviousNull_CurrentHasScope_HasDeltaAsync() {
    // Arrange
    var current = _createTestScopeContextFromScope(new PerspectiveScope { TenantId = "t1" });

    // Act
    var delta = ScopeDelta.CreateDelta(null, current);

    // Assert
    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Values).IsNotNull();
    await Assert.That(delta.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();
  }

  #endregion

  #region Additional Helper

  private static ScopeContext _createTestScopeContextFromScope(PerspectiveScope scope) =>
    new() {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

  #endregion
}
