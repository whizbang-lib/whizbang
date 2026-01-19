using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for SchemaConfiguration record - dual-prefix configuration.
/// Tests verify defaults and custom configuration values.
/// </summary>
public class SchemaConfigurationTests {
  [Test]
  public async Task SchemaConfiguration_WithoutParameters_UsesDefaultsAsync() {
    // Arrange & Act
    var config = new SchemaConfiguration();

    // Assert - Verify default values
    await Assert.That(config.InfrastructurePrefix).IsEqualTo("wh_");
    await Assert.That(config.PerspectivePrefix).IsEqualTo("wh_per_");
    await Assert.That(config.SchemaName).IsEqualTo("public");
    await Assert.That(config.Version).IsEqualTo(1);
  }

  [Test]
  public async Task SchemaConfiguration_WithCustomInfrastructurePrefix_SetsValueAsync() {
    // Arrange & Act
    var config = new SchemaConfiguration(InfrastructurePrefix: "infra_");

    // Assert
    await Assert.That(config.InfrastructurePrefix).IsEqualTo("infra_");
    await Assert.That(config.PerspectivePrefix).IsEqualTo("wh_per_"); // Others use defaults
  }

  [Test]
  public async Task SchemaConfiguration_WithCustomPerspectivePrefix_SetsValueAsync() {
    // Arrange & Act
    var config = new SchemaConfiguration(PerspectivePrefix: "view_");

    // Assert
    await Assert.That(config.PerspectivePrefix).IsEqualTo("view_");
    await Assert.That(config.InfrastructurePrefix).IsEqualTo("wh_"); // Others use defaults
  }

  [Test]
  public async Task SchemaConfiguration_WithCustomSchemaName_SetsValueAsync() {
    // Arrange & Act
    var config = new SchemaConfiguration(SchemaName: "app_schema");

    // Assert
    await Assert.That(config.SchemaName).IsEqualTo("app_schema");
  }

  [Test]
  public async Task SchemaConfiguration_WithCustomVersion_SetsValueAsync() {
    // Arrange & Act
    var config = new SchemaConfiguration(Version: 2);

    // Assert
    await Assert.That(config.Version).IsEqualTo(2);
  }

  [Test]
  public async Task SchemaConfiguration_WithAllCustom_SetsAllAsync() {
    // Arrange & Act
    var config = new SchemaConfiguration(
      InfrastructurePrefix: "sys_",
      PerspectivePrefix: "proj_",
      SchemaName: "custom",
      Version: 5
    );

    // Assert
    await Assert.That(config.InfrastructurePrefix).IsEqualTo("sys_");
    await Assert.That(config.PerspectivePrefix).IsEqualTo("proj_");
    await Assert.That(config.SchemaName).IsEqualTo("custom");
    await Assert.That(config.Version).IsEqualTo(5);
  }

  [Test]
  public async Task SchemaConfiguration_SameValues_AreEqualAsync() {
    // Arrange
    var config1 = new SchemaConfiguration(
      InfrastructurePrefix: "test_",
      Version: 2
    );

    var config2 = new SchemaConfiguration(
      InfrastructurePrefix: "test_",
      Version: 2
    );

    // Act & Assert
    await Assert.That(config1).IsEqualTo(config2);
  }

  [Test]
  public async Task SchemaConfiguration_DifferentPrefix_AreNotEqualAsync() {
    // Arrange
    var config1 = new SchemaConfiguration(InfrastructurePrefix: "a_");
    var config2 = new SchemaConfiguration(InfrastructurePrefix: "b_");

    // Act & Assert
    await Assert.That(config1).IsNotEqualTo(config2);
  }

  [Test]
  public async Task SchemaConfiguration_IsRecordAsync() {
    // Arrange & Act - Records have compiler-generated EqualityContract property
    var hasEqualityContract = typeof(SchemaConfiguration).GetProperty("EqualityContract",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;

    // Assert
    await Assert.That(hasEqualityContract).IsTrue();
  }
}
