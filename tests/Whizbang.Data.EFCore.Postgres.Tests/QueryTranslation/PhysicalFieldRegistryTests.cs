using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Unit tests for <see cref="PhysicalFieldRegistry"/>.
/// </summary>
[NotInParallel("PhysicalFieldRegistry")]
public class PhysicalFieldRegistryTests {
  // Test model for registration
  public class TestModel {
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
  }

  public class OtherModel {
    public string Title { get; init; } = string.Empty;
  }

  [Before(Test)]
  public void Setup() {
    // Clear registry before each test
    PhysicalFieldRegistry.Clear();
  }

  [Test]
  public async Task Register_WithValidParameters_AddsMapping() {
    // Act
    PhysicalFieldRegistry.Register<TestModel>("Price", "price");

    // Assert
    await Assert.That(PhysicalFieldRegistry.Count).IsEqualTo(1);
    await Assert.That(PhysicalFieldRegistry.IsPhysicalField(typeof(TestModel), "Price")).IsTrue();
  }

  [Test]
  public async Task Register_WithShadowPropertyName_StoresBothNames() {
    // Act
    PhysicalFieldRegistry.Register<TestModel>("IsActive", "is_active", "is_active");

    // Assert
    var found = PhysicalFieldRegistry.TryGetMapping(typeof(TestModel), "IsActive", out var mapping);
    await Assert.That(found).IsTrue();
    await Assert.That(mapping.ColumnName).IsEqualTo("is_active");
    await Assert.That(mapping.ShadowPropertyName).IsEqualTo("is_active");
  }

  [Test]
  public async Task TryGetMapping_WhenRegistered_ReturnsMapping() {
    // Arrange
    PhysicalFieldRegistry.Register<TestModel>("Name", "name");

    // Act
    var found = PhysicalFieldRegistry.TryGetMapping(typeof(TestModel), "Name", out var mapping);

    // Assert
    await Assert.That(found).IsTrue();
    await Assert.That(mapping.ColumnName).IsEqualTo("name");
    await Assert.That(mapping.ShadowPropertyName).IsEqualTo("name");
  }

  [Test]
  public async Task TryGetMapping_WhenNotRegistered_ReturnsFalse() {
    // Act
    var found = PhysicalFieldRegistry.TryGetMapping(typeof(TestModel), "NotRegistered", out _);

    // Assert
    await Assert.That(found).IsFalse();
  }

  [Test]
  public async Task IsPhysicalField_WhenRegistered_ReturnsTrue() {
    // Arrange
    PhysicalFieldRegistry.Register<TestModel>("Price", "price");

    // Act & Assert
    await Assert.That(PhysicalFieldRegistry.IsPhysicalField(typeof(TestModel), "Price")).IsTrue();
  }

  [Test]
  public async Task IsPhysicalField_WhenNotRegistered_ReturnsFalse() {
    // Act & Assert
    await Assert.That(PhysicalFieldRegistry.IsPhysicalField(typeof(TestModel), "Price")).IsFalse();
  }

  [Test]
  public async Task IsPhysicalField_DifferentModel_ReturnsFalse() {
    // Arrange - register for TestModel
    PhysicalFieldRegistry.Register<TestModel>("Name", "name");

    // Act & Assert - different model should not match
    await Assert.That(PhysicalFieldRegistry.IsPhysicalField(typeof(OtherModel), "Name")).IsFalse();
  }

  [Test]
  public async Task GetMappingsForModel_ReturnsOnlyModelMappings() {
    // Arrange
    PhysicalFieldRegistry.Register<TestModel>("Name", "name");
    PhysicalFieldRegistry.Register<TestModel>("Price", "price");
    PhysicalFieldRegistry.Register<OtherModel>("Title", "title");

    // Act
    var mappings = PhysicalFieldRegistry.GetMappingsForModel(typeof(TestModel));

    // Assert
    await Assert.That(mappings).Count().IsEqualTo(2);
    await Assert.That(mappings.ContainsKey("Name")).IsTrue();
    await Assert.That(mappings.ContainsKey("Price")).IsTrue();
    await Assert.That(mappings.ContainsKey("Title")).IsFalse();
  }

  [Test]
  public async Task Clear_RemovesAllMappings() {
    // Arrange
    PhysicalFieldRegistry.Register<TestModel>("Name", "name");
    PhysicalFieldRegistry.Register<TestModel>("Price", "price");

    // Act
    PhysicalFieldRegistry.Clear();

    // Assert
    await Assert.That(PhysicalFieldRegistry.Count).IsEqualTo(0);
  }

  [Test]
  public void Register_WithNullModelType_ThrowsArgumentNullException() {
    // Act & Assert
    Assert.Throws<ArgumentNullException>(() => PhysicalFieldRegistry.Register(null!, "Name", "name"));
  }

  [Test]
  public void Register_WithNullPropertyName_ThrowsArgumentException() {
    // Act & Assert
    Assert.Throws<ArgumentException>(() => PhysicalFieldRegistry.Register<TestModel>(null!, "name"));
  }

  [Test]
  public void Register_WithEmptyColumnName_ThrowsArgumentException() {
    // Act & Assert
    Assert.Throws<ArgumentException>(() => PhysicalFieldRegistry.Register<TestModel>("Name", ""));
  }

  [Test]
  public async Task Register_OverwritesExistingMapping() {
    // Arrange
    PhysicalFieldRegistry.Register<TestModel>("Name", "old_name");

    // Act
    PhysicalFieldRegistry.Register<TestModel>("Name", "new_name");

    // Assert
    var found = PhysicalFieldRegistry.TryGetMapping(typeof(TestModel), "Name", out var mapping);
    await Assert.That(found).IsTrue();
    await Assert.That(mapping.ColumnName).IsEqualTo("new_name");
  }

  [Test]
  public async Task NonGenericRegister_WorksCorrectlyAsync() {
    // Act - Use non-generic version explicitly for runtime registration scenarios
#pragma warning disable CA2263 // Prefer generic overload - testing non-generic path intentionally
    PhysicalFieldRegistry.Register(typeof(TestModel), "Name", "name");
#pragma warning restore CA2263

    // Assert
    await Assert.That(PhysicalFieldRegistry.IsPhysicalField(typeof(TestModel), "Name")).IsTrue();
  }
}
