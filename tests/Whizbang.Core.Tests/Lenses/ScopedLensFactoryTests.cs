using System;
using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for <see cref="IScopedLensFactory"/> and related types.
/// Validates the factory pattern for tenant/user-scoped lens queries.
/// </summary>
/// <tests>Whizbang.Core/Lenses/IScopedLensFactory.cs</tests>
[Category("Core")]
[Category("Lenses")]
public class ScopedLensFactoryTests {

  [Test]
  public async Task IScopedLensFactory_IsInterfaceAsync() {
    // Assert
    await Assert.That(typeof(IScopedLensFactory).IsInterface).IsTrue();
  }

  [Test]
  public async Task IScopedLensFactory_HasGetLensMethodAsync() {
    // Arrange
    var method = typeof(IScopedLensFactory).GetMethod("GetLens");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.IsGenericMethod).IsTrue();
  }

  [Test]
  public async Task FilterMode_HasEqualsValueAsync() {
    // Assert
    await Assert.That(Enum.IsDefined(FilterMode.Equals)).IsTrue();
  }

  [Test]
  public async Task FilterMode_HasInValueAsync() {
    // Assert
    await Assert.That(Enum.IsDefined(FilterMode.In)).IsTrue();
  }

  [Test]
  public async Task FilterMode_DefaultIsEqualsAsync() {
    // Arrange
    var defaultValue = default(FilterMode);

    // Assert
    await Assert.That(defaultValue).IsEqualTo(FilterMode.Equals);
  }

  [Test]
  public async Task ScopeDefinition_Name_CanBeSetAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Tenant");

    // Assert
    await Assert.That(definition.Name).IsEqualTo("Tenant");
  }

  [Test]
  public async Task ScopeDefinition_FilterPropertyName_CanBeSetAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Tenant") {
      FilterPropertyName = "TenantId"
    };

    // Assert
    await Assert.That(definition.FilterPropertyName).IsEqualTo("TenantId");
  }

  [Test]
  public async Task ScopeDefinition_ContextKey_CanBeSetAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Tenant") {
      ContextKey = "TenantId"
    };

    // Assert
    await Assert.That(definition.ContextKey).IsEqualTo("TenantId");
  }

  [Test]
  public async Task ScopeDefinition_FilterMode_DefaultsToEqualsAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Tenant");

    // Assert
    await Assert.That(definition.FilterMode).IsEqualTo(FilterMode.Equals);
  }

  [Test]
  public async Task ScopeDefinition_FilterMode_CanBeSetToInAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("TenantHierarchy") {
      FilterMode = FilterMode.In
    };

    // Assert
    await Assert.That(definition.FilterMode).IsEqualTo(FilterMode.In);
  }

  [Test]
  public async Task ScopeDefinition_NoFilter_CanBeSetAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Global") {
      NoFilter = true
    };

    // Assert
    await Assert.That(definition.NoFilter).IsTrue();
  }

  [Test]
  public async Task ScopeDefinition_NoFilter_DefaultsToFalseAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Tenant");

    // Assert
    await Assert.That(definition.NoFilter).IsFalse();
  }

  [Test]
  public async Task ScopeDefinition_FilterInterfaceType_CanBeSetAsync() {
    // Arrange & Act
    var definition = new ScopeDefinition("Tenant") {
      FilterInterfaceType = typeof(ITenantScoped)
    };

    // Assert
    await Assert.That(definition.FilterInterfaceType).IsEqualTo(typeof(ITenantScoped));
  }

  [Test]
  public async Task LensOptions_Scopes_IsEmptyByDefaultAsync() {
    // Arrange & Act
    var options = new LensOptions();

    // Assert
    await Assert.That(options.Scopes).IsEmpty();
  }

  [Test]
  public async Task LensOptions_DefineScope_AddsScopeDefinitionAsync() {
    // Arrange
    var options = new LensOptions();

    // Act
    options.DefineScope("Tenant", scope => {
      scope.FilterPropertyName = "TenantId";
      scope.ContextKey = "TenantId";
    });

    // Assert
    await Assert.That(options.Scopes.Count).IsEqualTo(1);
    await Assert.That(options.Scopes[0].Name).IsEqualTo("Tenant");
  }

  [Test]
  public async Task LensOptions_DefineScope_ReturnsSameInstanceForChainingAsync() {
    // Arrange
    var options = new LensOptions();

    // Act
    var result = options.DefineScope("Tenant", _ => { });

    // Assert
    await Assert.That(result).IsEqualTo(options);
  }

  [Test]
  public async Task LensOptions_DefineScope_AllowsMultipleScopesAsync() {
    // Arrange
    var options = new LensOptions();

    // Act
    options
      .DefineScope("Tenant", scope => scope.FilterPropertyName = "TenantId")
      .DefineScope("User", scope => scope.FilterPropertyName = "UserId")
      .DefineScope("Global", scope => scope.NoFilter = true);

    // Assert
    await Assert.That(options.Scopes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task LensOptions_DefineScope_ConfiguresFilterModeAsync() {
    // Arrange
    var options = new LensOptions();

    // Act
    options.DefineScope("TenantHierarchy", scope => {
      scope.FilterPropertyName = "TenantId";
      scope.FilterMode = FilterMode.In;
    });

    // Assert
    await Assert.That(options.Scopes[0].FilterMode).IsEqualTo(FilterMode.In);
  }

  [Test]
  public async Task LensOptions_GetScope_ReturnsDefinedScopeAsync() {
    // Arrange
    var options = new LensOptions();
    options.DefineScope("Tenant", scope => scope.FilterPropertyName = "TenantId");

    // Act
    var scope = options.GetScope("Tenant");

    // Assert
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.Name).IsEqualTo("Tenant");
  }

  [Test]
  public async Task LensOptions_GetScope_ReturnsNullForUndefinedScopeAsync() {
    // Arrange
    var options = new LensOptions();

    // Act
    var scope = options.GetScope("NonExistent");

    // Assert
    await Assert.That(scope is null).IsTrue();
  }

  [Test]
  public async Task LensOptions_GetScope_IsCaseInsensitiveAsync() {
    // Arrange
    var options = new LensOptions();
    options.DefineScope("Tenant", scope => scope.FilterPropertyName = "TenantId");

    // Act
    var scope = options.GetScope("tenant");

    // Assert
    await Assert.That(scope).IsNotNull();
  }

  // Test interface for tenant scoping
  public interface ITenantScoped {
    string TenantId { get; }
  }

  // Test interface for user scoping
  public interface IUserScoped {
    string UserId { get; }
  }
}
