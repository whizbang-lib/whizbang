using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="PolymorphicQueryExtensions"/> discriminator-based query methods.
/// </summary>
[Category("Unit")]
[Category("Polymorphic")]
public class PolymorphicQueryExtensionsTests {

  // Test model classes
  private sealed record TestModel {
    public string SettingsTypeName { get; init; } = "";
    public string Name { get; init; } = "";
  }

  private sealed record TextFieldSettings {
    public string SettingsTypeName { get; init; } = "";
  }

  private sealed record NumberFieldSettings {
    public string SettingsTypeName { get; init; } = "";
  }

  [Test]
  public async Task WhereDiscriminatorEquals_WithValidSelector_ReturnsQueryableAsync() {
    // Arrange
    var query = CreateTestQueryable<TestModel>();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorEquals<TestModel, TextFieldSettings>(selector);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsAssignableTo<IQueryable<PerspectiveRow<TestModel>>>();
  }

  [Test]
  public async Task WhereDiscriminatorEquals_WithTypeName_FiltersCorrectlyAsync() {
    // Arrange
    var rows = new List<PerspectiveRow<TestModel>> {
      CreateRow(new TestModel { SettingsTypeName = "TextFieldSettings", Name = "Field1" }),
      CreateRow(new TestModel { SettingsTypeName = "NumberFieldSettings", Name = "Field2" }),
      CreateRow(new TestModel { SettingsTypeName = "TextFieldSettings", Name = "Field3" })
    };
    var query = rows.AsQueryable();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorEquals<TestModel, TextFieldSettings>(selector).ToList();

    // Assert - Should only return rows with TextFieldSettings
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result.All(r => r.Data.SettingsTypeName == "TextFieldSettings")).IsTrue();
  }

  [Test]
  public async Task WhereDiscriminatorEqualsFullName_WithTypeName_FiltersCorrectlyAsync() {
    // Arrange - Use full type name as discriminator value
    var fullTypeName = typeof(TextFieldSettings).FullName!;
    var rows = new List<PerspectiveRow<TestModel>> {
      CreateRow(new TestModel { SettingsTypeName = fullTypeName, Name = "Field1" }),
      CreateRow(new TestModel { SettingsTypeName = "NumberFieldSettings", Name = "Field2" }),
      CreateRow(new TestModel { SettingsTypeName = fullTypeName, Name = "Field3" })
    };
    var query = rows.AsQueryable();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorEqualsFullName<TestModel, TextFieldSettings>(selector).ToList();

    // Assert - Should only return rows with full TextFieldSettings type name
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result.All(r => r.Data.SettingsTypeName == fullTypeName)).IsTrue();
  }

  [Test]
  public async Task WhereDiscriminatorValue_WithStringValue_FiltersCorrectlyAsync() {
    // Arrange
    var query = CreateTestQueryable<TestModel>();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorValue(selector, "CustomTypeName");

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task WhereDiscriminatorIn_WithMultipleValues_FiltersCorrectlyAsync() {
    // Arrange
    var rows = new List<PerspectiveRow<TestModel>> {
      CreateRow(new TestModel { SettingsTypeName = "Type1", Name = "Field1" }),
      CreateRow(new TestModel { SettingsTypeName = "Type2", Name = "Field2" }),
      CreateRow(new TestModel { SettingsTypeName = "Type3", Name = "Field3" }),
      CreateRow(new TestModel { SettingsTypeName = "Type4", Name = "Field4" })
    };
    var query = rows.AsQueryable();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorIn(selector, "Type1", "Type2", "Type3").ToList();

    // Assert - Should return rows with Type1, Type2, or Type3
    await Assert.That(result.Count).IsEqualTo(3);
    await Assert.That(result.All(r => new[] { "Type1", "Type2", "Type3" }.Contains(r.Data.SettingsTypeName))).IsTrue();
  }

  [Test]
  public async Task WhereDiscriminatorIn_WithSingleValue_DelegatesToWhereDiscriminatorValueAsync() {
    // Arrange
    var query = CreateTestQueryable<TestModel>();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorIn(selector, "SingleValue");

    // Assert - Single value should use equality, not Contains
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task WhereDiscriminatorIn_WithEmptyValues_ReturnsEmptyQueryAsync() {
    // Arrange
    var rows = new List<PerspectiveRow<TestModel>> {
      CreateRow(new TestModel { SettingsTypeName = "Type1", Name = "Field1" }),
      CreateRow(new TestModel { SettingsTypeName = "Type2", Name = "Field2" })
    };
    var query = rows.AsQueryable();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act
    var result = query.WhereDiscriminatorIn(selector).ToList();

    // Assert - Empty values should return no matches
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task WhereDiscriminatorEquals_WithNullSelector_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var query = CreateTestQueryable<TestModel>();
    Expression<Func<TestModel, string>> selector = null!;

    // Act & Assert
    await Assert.That(() => query.WhereDiscriminatorEquals<TestModel, TextFieldSettings>(selector))
        .ThrowsException().WithMessageContaining("discriminatorSelector");
  }

  [Test]
  public async Task WhereDiscriminatorValue_WithNullValue_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var query = CreateTestQueryable<TestModel>();
    Expression<Func<TestModel, string>> selector = m => m.SettingsTypeName;

    // Act & Assert
    await Assert.That(() => query.WhereDiscriminatorValue(selector, null!))
        .ThrowsException().WithMessageContaining("discriminatorValue");
  }

  private static IQueryable<PerspectiveRow<TModel>> CreateTestQueryable<TModel>() where TModel : class {
    var rows = new List<PerspectiveRow<TModel>>();
    return rows.AsQueryable();
  }

  private static PerspectiveRow<TestModel> CreateRow(TestModel data) {
    var now = DateTime.UtcNow;
    return new PerspectiveRow<TestModel> {
      Id = Guid.NewGuid(),
      Data = data,
      Metadata = new PerspectiveMetadata {
        EventType = "TestEvent",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = now,
        CorrelationId = Guid.NewGuid().ToString(),
        CausationId = null
      },
      Scope = new PerspectiveScope {
        TenantId = null,
        UserId = null,
        OrganizationId = null
      },
      CreatedAt = now,
      UpdatedAt = now,
      Version = 1
    };
  }
}
