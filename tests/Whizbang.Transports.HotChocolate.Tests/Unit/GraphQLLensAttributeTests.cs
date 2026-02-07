using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="GraphQLLensAttribute"/>.
/// Verifies attribute configuration and defaults.
/// </summary>
public class GraphQLLensAttributeTests {
  [Test]
  public async Task Constructor_ShouldSetDefaultsAsync() {
    // Arrange & Act
    var attribute = new GraphQLLensAttribute();

    // Assert - verify all defaults
    await Assert.That(attribute.QueryName).IsNull();
    await Assert.That(attribute.Scope).IsEqualTo(GraphQLLensScope.Default);
    await Assert.That(attribute.EnableFiltering).IsTrue();
    await Assert.That(attribute.EnableSorting).IsTrue();
    await Assert.That(attribute.EnablePaging).IsTrue();
    await Assert.That(attribute.EnableProjection).IsTrue();
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(10);
    await Assert.That(attribute.MaxPageSize).IsEqualTo(100);
  }

  [Test]
  public async Task QueryName_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.QueryName = "orders";

    // Assert
    await Assert.That(attribute.QueryName).IsEqualTo("orders");
  }

  [Test]
  public async Task Scope_ShouldBeSettableToDataOnlyAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.Scope = GraphQLLensScope.DataOnly;

    // Assert
    await Assert.That(attribute.Scope).IsEqualTo(GraphQLLensScope.DataOnly);
  }

  [Test]
  public async Task Scope_ShouldBeSettableToAllAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.Scope = GraphQLLensScope.All;

    // Assert
    await Assert.That(attribute.Scope).IsEqualTo(GraphQLLensScope.All);
  }

  [Test]
  public async Task Scope_ShouldBeSettableToComposedFlagsAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.Scope = GraphQLLensScope.Data | GraphQLLensScope.Metadata | GraphQLLensScope.SystemFields;
    var hasData = attribute.Scope.HasFlag(GraphQLLensScope.Data);
    var hasMetadata = attribute.Scope.HasFlag(GraphQLLensScope.Metadata);
    var hasSystemFields = attribute.Scope.HasFlag(GraphQLLensScope.SystemFields);
    var hasScope = attribute.Scope.HasFlag(GraphQLLensScope.Scope);

    // Assert
    await Assert.That(hasData).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
    await Assert.That(hasScope).IsFalse();
  }

  [Test]
  public async Task EnableFiltering_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.EnableFiltering = false;

    // Assert
    await Assert.That(attribute.EnableFiltering).IsFalse();
  }

  [Test]
  public async Task EnableSorting_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.EnableSorting = false;

    // Assert
    await Assert.That(attribute.EnableSorting).IsFalse();
  }

  [Test]
  public async Task EnablePaging_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.EnablePaging = false;

    // Assert
    await Assert.That(attribute.EnablePaging).IsFalse();
  }

  [Test]
  public async Task EnableProjection_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.EnableProjection = false;

    // Assert
    await Assert.That(attribute.EnableProjection).IsFalse();
  }

  [Test]
  public async Task DefaultPageSize_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.DefaultPageSize = 25;

    // Assert
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(25);
  }

  [Test]
  public async Task MaxPageSize_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.MaxPageSize = 500;

    // Assert
    await Assert.That(attribute.MaxPageSize).IsEqualTo(500);
  }

  [Test]
  public async Task Attribute_ShouldBeApplicableToClassesAndInterfacesAsync() {
    // Arrange - reflection to check AttributeUsage
    var attributeType = typeof(GraphQLLensAttribute);
    var usage = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(usage).IsNotNull();
    var validOnClass = usage!.ValidOn.HasFlag(AttributeTargets.Class);
    var validOnInterface = usage.ValidOn.HasFlag(AttributeTargets.Interface);
    await Assert.That(validOnClass).IsTrue();
    await Assert.That(validOnInterface).IsTrue();
  }

  [Test]
  public async Task Scope_ShouldBeSettableToNoDataAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.Scope = GraphQLLensScope.NoData;
    var hasMetadata = attribute.Scope.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = attribute.Scope.HasFlag(GraphQLLensScope.Scope);
    var hasSystemFields = attribute.Scope.HasFlag(GraphQLLensScope.SystemFields);
    var hasData = attribute.Scope.HasFlag(GraphQLLensScope.Data);

    // Assert
    await Assert.That(attribute.Scope).IsEqualTo(GraphQLLensScope.NoData);
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasScope).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
    await Assert.That(hasData).IsFalse();
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange & Act
    var attribute = new GraphQLLensAttribute {
      QueryName = "products",
      Scope = GraphQLLensScope.Data | GraphQLLensScope.SystemFields,
      EnableFiltering = false,
      EnableSorting = false,
      EnablePaging = false,
      EnableProjection = false,
      DefaultPageSize = 50,
      MaxPageSize = 200
    };

    // Assert
    await Assert.That(attribute.QueryName).IsEqualTo("products");
    await Assert.That(attribute.Scope).IsEqualTo(GraphQLLensScope.Data | GraphQLLensScope.SystemFields);
    await Assert.That(attribute.EnableFiltering).IsFalse();
    await Assert.That(attribute.EnableSorting).IsFalse();
    await Assert.That(attribute.EnablePaging).IsFalse();
    await Assert.That(attribute.EnableProjection).IsFalse();
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(50);
    await Assert.That(attribute.MaxPageSize).IsEqualTo(200);
  }

  [Test]
  public async Task QueryName_EmptyString_ShouldBeAllowedAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.QueryName = "";

    // Assert
    await Assert.That(attribute.QueryName).IsEqualTo("");
  }

  [Test]
  public async Task DefaultPageSize_Zero_ShouldBeAllowedAsync() {
    // Arrange
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.DefaultPageSize = 0;

    // Assert
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(0);
  }

  [Test]
  public async Task MaxPageSize_LessThanDefault_ShouldBeAllowedAsync() {
    // Arrange - attribute doesn't enforce validation, that's done at runtime
    var attribute = new GraphQLLensAttribute();

    // Act
    attribute.DefaultPageSize = 100;
    attribute.MaxPageSize = 50;

    // Assert - values are stored as-is (validation at runtime)
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(100);
    await Assert.That(attribute.MaxPageSize).IsEqualTo(50);
  }

  [Test]
  public async Task Attribute_ShouldInheritFromAttributeAsync() {
    // Assert
    var isSubclass = typeof(GraphQLLensAttribute).IsSubclassOf(typeof(Attribute));
    await Assert.That(isSubclass).IsTrue();
  }

  [Test]
  public async Task Attribute_ShouldNotAllowMultipleAsync() {
    // Arrange
    var attributeType = typeof(GraphQLLensAttribute);
    var usage = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - should not allow multiple (default is false)
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.AllowMultiple).IsFalse();
  }
}
