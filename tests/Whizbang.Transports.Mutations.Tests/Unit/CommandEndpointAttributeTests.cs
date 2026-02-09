using Whizbang.Core;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.Mutations.Tests.Unit;

/// <summary>
/// Tests for <see cref="CommandEndpointAttribute{TCommand, TResult}"/>.
/// Verifies attribute configuration, defaults, and generic type constraints.
/// </summary>
public class CommandEndpointAttributeTests {
  [Test]
  public async Task Constructor_ShouldSetDefaultsAsync() {
    // Arrange & Act
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Assert - verify all defaults
    await Assert.That(attribute.RestRoute).IsNull();
    await Assert.That(attribute.GraphQLMutation).IsNull();
    await Assert.That(attribute.RequestType).IsNull();
  }

  [Test]
  public async Task RestRoute_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.RestRoute = "/api/orders";

    // Assert
    await Assert.That(attribute.RestRoute).IsEqualTo("/api/orders");
  }

  [Test]
  public async Task GraphQLMutation_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.GraphQLMutation = "createOrder";

    // Assert
    await Assert.That(attribute.GraphQLMutation).IsEqualTo("createOrder");
  }

  [Test]
  public async Task RequestType_ShouldBeSettableAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.RequestType = typeof(TestRequest);

    // Assert
    await Assert.That(attribute.RequestType).IsEqualTo(typeof(TestRequest));
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange & Act
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult> {
      RestRoute = "/api/products",
      GraphQLMutation = "createProduct",
      RequestType = typeof(TestRequest)
    };

    // Assert
    await Assert.That(attribute.RestRoute).IsEqualTo("/api/products");
    await Assert.That(attribute.GraphQLMutation).IsEqualTo("createProduct");
    await Assert.That(attribute.RequestType).IsEqualTo(typeof(TestRequest));
  }

  [Test]
  public async Task RestRoute_EmptyString_ShouldBeAllowedAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.RestRoute = "";

    // Assert
    await Assert.That(attribute.RestRoute).IsEqualTo("");
  }

  [Test]
  public async Task GraphQLMutation_EmptyString_ShouldBeAllowedAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.GraphQLMutation = "";

    // Assert
    await Assert.That(attribute.GraphQLMutation).IsEqualTo("");
  }

  [Test]
  public async Task Attribute_ShouldInheritFromAttributeAsync() {
    // Assert
    var isSubclass = typeof(CommandEndpointAttribute<TestCommand, TestResult>).IsSubclassOf(typeof(Attribute));
    await Assert.That(isSubclass).IsTrue();
  }

  [Test]
  public async Task Attribute_ShouldBeApplicableToClassesOnlyAsync() {
    // Arrange - reflection to check AttributeUsage
    var attributeType = typeof(CommandEndpointAttribute<TestCommand, TestResult>);
    var usage = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(usage).IsNotNull();
    var validOnClass = usage!.ValidOn.HasFlag(AttributeTargets.Class);
    await Assert.That(validOnClass).IsTrue();
  }

  [Test]
  public async Task Attribute_ShouldNotAllowMultipleAsync() {
    // Arrange
    var attributeType = typeof(CommandEndpointAttribute<TestCommand, TestResult>);
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
    var attributeType = typeof(CommandEndpointAttribute<TestCommand, TestResult>);
    var usage = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - should not inherit
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.Inherited).IsFalse();
  }

  [Test]
  public async Task CommandType_ShouldBeAccessibleViaGenericParameterAsync() {
    // Arrange - closed generic type
    var closedGenericType = typeof(CommandEndpointAttribute<TestCommand, TestResult>);

    // Act
    var genericArguments = closedGenericType.GetGenericArguments();

    // Assert - closed generic should have the concrete type arguments
    await Assert.That(closedGenericType.IsGenericType).IsTrue();
    await Assert.That(genericArguments).Count().IsEqualTo(2);
    await Assert.That(genericArguments[0]).IsEqualTo(typeof(TestCommand));
    await Assert.That(genericArguments[1]).IsEqualTo(typeof(TestResult));
  }

  [Test]
  public async Task OpenGenericType_ShouldHaveTwoTypeParametersAsync() {
    // Arrange
    var openGenericType = typeof(CommandEndpointAttribute<,>);

    // Act
    var genericArguments = openGenericType.GetGenericArguments();

    // Assert
    await Assert.That(genericArguments).Count().IsEqualTo(2);
    await Assert.That(genericArguments[0].Name).IsEqualTo("TCommand");
    await Assert.That(genericArguments[1].Name).IsEqualTo("TResult");
  }

  [Test]
  public async Task TCommand_ShouldBeConstrainedToICommandAsync() {
    // Arrange
    var openGenericType = typeof(CommandEndpointAttribute<,>);
    var commandParameter = openGenericType.GetGenericArguments()[0];

    // Act
    var constraints = commandParameter.GetGenericParameterConstraints();

    // Assert - TCommand should be constrained to ICommand
    await Assert.That(constraints).Count().IsEqualTo(1);
    await Assert.That(constraints[0].Name).IsEqualTo("ICommand");
  }

  [Test]
  public async Task RestRoute_WithSlash_ShouldRetainSlashAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.RestRoute = "/api/v1/orders/{id}";

    // Assert - path parameters preserved
    await Assert.That(attribute.RestRoute).IsEqualTo("/api/v1/orders/{id}");
  }

  [Test]
  public async Task GraphQLMutation_WithCamelCase_ShouldRetainCasingAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult>();

    // Act
    attribute.GraphQLMutation = "createOrderWithItems";

    // Assert
    await Assert.That(attribute.GraphQLMutation).IsEqualTo("createOrderWithItems");
  }

  [Test]
  public async Task RequestType_CanBeSetToNull_ExplicitlyAsync() {
    // Arrange
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult> {
      RequestType = typeof(TestRequest)
    };

    // Act
    attribute.RequestType = null;

    // Assert
    await Assert.That(attribute.RequestType).IsNull();
  }

  [Test]
  public async Task Attribute_WithOnlyRestRoute_ShouldWorkAsync() {
    // Arrange & Act - REST-only endpoint
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult> {
      RestRoute = "/api/orders"
    };

    // Assert
    await Assert.That(attribute.RestRoute).IsEqualTo("/api/orders");
    await Assert.That(attribute.GraphQLMutation).IsNull();
  }

  [Test]
  public async Task Attribute_WithOnlyGraphQLMutation_ShouldWorkAsync() {
    // Arrange & Act - GraphQL-only endpoint
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult> {
      GraphQLMutation = "createOrder"
    };

    // Assert
    await Assert.That(attribute.GraphQLMutation).IsEqualTo("createOrder");
    await Assert.That(attribute.RestRoute).IsNull();
  }

  [Test]
  public async Task Attribute_WithBothRoutes_ShouldWorkAsync() {
    // Arrange & Act - dual transport
    var attribute = new CommandEndpointAttribute<TestCommand, TestResult> {
      RestRoute = "/api/orders",
      GraphQLMutation = "createOrder"
    };

    // Assert
    await Assert.That(attribute.RestRoute).IsEqualTo("/api/orders");
    await Assert.That(attribute.GraphQLMutation).IsEqualTo("createOrder");
  }
}

// Test types for attribute tests
public class TestCommand : ICommand { }
public class TestResult { public string? Value { get; set; } }
public class TestRequest { public string? Data { get; set; } }
