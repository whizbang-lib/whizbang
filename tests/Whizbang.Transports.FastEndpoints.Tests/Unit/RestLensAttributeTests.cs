using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="RestLensAttribute"/>.
/// Verifies attribute configuration and defaults mirror <see cref="GraphQLLensAttribute"/>.
/// </summary>
public class RestLensAttributeTests {
  [Test]
  public async Task Constructor_ShouldSetDefaultsAsync() {
    // Arrange & Act
    var attribute = new RestLensAttribute();

    // Assert - verify all defaults mirror GraphQLLensAttribute
    await Assert.That(attribute.Route).IsNull();
    await Assert.That(attribute.EnableFiltering).IsTrue();
    await Assert.That(attribute.EnableSorting).IsTrue();
    await Assert.That(attribute.EnablePaging).IsTrue();
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(10);
    await Assert.That(attribute.MaxPageSize).IsEqualTo(100);
  }

  [Test]
  public async Task Route_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.Route = "/api/orders";

    // Assert
    await Assert.That(attribute.Route).IsEqualTo("/api/orders");
  }

  [Test]
  public async Task Route_WithPathParameters_ShouldRetainParametersAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.Route = "/api/v1/orders/{id}";

    // Assert
    await Assert.That(attribute.Route).IsEqualTo("/api/v1/orders/{id}");
  }

  [Test]
  public async Task EnableFiltering_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.EnableFiltering = false;

    // Assert
    await Assert.That(attribute.EnableFiltering).IsFalse();
  }

  [Test]
  public async Task EnableSorting_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.EnableSorting = false;

    // Assert
    await Assert.That(attribute.EnableSorting).IsFalse();
  }

  [Test]
  public async Task EnablePaging_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.EnablePaging = false;

    // Assert
    await Assert.That(attribute.EnablePaging).IsFalse();
  }

  [Test]
  public async Task DefaultPageSize_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.DefaultPageSize = 25;

    // Assert
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(25);
  }

  [Test]
  public async Task MaxPageSize_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.MaxPageSize = 500;

    // Assert
    await Assert.That(attribute.MaxPageSize).IsEqualTo(500);
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange & Act
    var attribute = new RestLensAttribute {
      Route = "/api/products",
      EnableFiltering = false,
      EnableSorting = false,
      EnablePaging = false,
      DefaultPageSize = 50,
      MaxPageSize = 200
    };

    // Assert
    await Assert.That(attribute.Route).IsEqualTo("/api/products");
    await Assert.That(attribute.EnableFiltering).IsFalse();
    await Assert.That(attribute.EnableSorting).IsFalse();
    await Assert.That(attribute.EnablePaging).IsFalse();
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(50);
    await Assert.That(attribute.MaxPageSize).IsEqualTo(200);
  }

  [Test]
  public async Task Route_EmptyString_ShouldBeAllowedAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.Route = "";

    // Assert
    await Assert.That(attribute.Route).IsEqualTo("");
  }

  [Test]
  public async Task DefaultPageSize_Zero_ShouldBeAllowedAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.DefaultPageSize = 0;

    // Assert
    await Assert.That(attribute.DefaultPageSize).IsEqualTo(0);
  }

  [Test]
  public async Task MaxPageSize_LessThanDefault_ShouldBeAllowedAsync() {
    // Arrange - attribute doesn't enforce validation, that's done at runtime
    var attribute = new RestLensAttribute();

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
    var isSubclass = typeof(RestLensAttribute).IsSubclassOf(typeof(Attribute));
    await Assert.That(isSubclass).IsTrue();
  }

  [Test]
  public async Task Attribute_ShouldBeApplicableToClassesAndInterfacesAsync() {
    // Arrange - reflection to check AttributeUsage
    var attributeType = typeof(RestLensAttribute);
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
  public async Task Attribute_ShouldNotAllowMultipleAsync() {
    // Arrange
    var attributeType = typeof(RestLensAttribute);
    var usage = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - should not allow multiple (default is false)
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task Attribute_ShouldNotInheritAsync() {
    // Arrange
    var attributeType = typeof(RestLensAttribute);
    var usage = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - should not inherit
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.Inherited).IsFalse();
  }

  [Test]
  public async Task Route_WithQueryString_ShouldRetainQueryStringAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act - route templates shouldn't have query strings, but attribute stores as-is
    attribute.Route = "/api/orders?status=active";

    // Assert
    await Assert.That(attribute.Route).IsEqualTo("/api/orders?status=active");
  }

  [Test]
  public async Task Route_WithVersionPrefix_ShouldRetainVersionAsync() {
    // Arrange
    var attribute = new RestLensAttribute();

    // Act
    attribute.Route = "/api/v2/orders";

    // Assert
    await Assert.That(attribute.Route).IsEqualTo("/api/v2/orders");
  }
}
