using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Whizbang.Transports.FastEndpoints.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for RestMutationEndpointGenerator.
/// Validates discovery of [CommandEndpoint] attributes with RestRoute
/// and generation of FastEndpoints mutation endpoint classes.
/// </summary>
/// <tests>Whizbang.Transports.FastEndpoints.Generators/RestMutationEndpointGenerator.cs</tests>
[Category("Generators")]
[Category("FastEndpoints")]
[Category("Mutations")]
public class RestMutationEndpointGeneratorTests {

  /// <summary>
  /// Test that generator discovers command with [CommandEndpoint] and RestRoute generates endpoint.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCommandEndpointAndRestRoute_GeneratesEndpointAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult {
                public Guid OrderId { get; set; }
                public bool Success { get; set; }
            }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand {
                public required string CustomerId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrderCommandEndpoint");
    await Assert.That(code).Contains("CreateOrderCommand");
  }

  /// <summary>
  /// Test that generator handles multiple commands with RestRoute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleCommands_GeneratesAllEndpointsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }
            public class ProductResult { public Guid Id { get; set; } }
            public class CustomerResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }

            [CommandEndpoint<CreateProductCommand, ProductResult>(RestRoute = "/api/products")]
            public class CreateProductCommand : ICommand { }

            [CommandEndpoint<CreateCustomerCommand, CustomerResult>(RestRoute = "/api/customers")]
            public class CreateCustomerCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrderCommandEndpoint");
    await Assert.That(code).Contains("CreateProductCommandEndpoint");
    await Assert.That(code).Contains("CreateCustomerCommandEndpoint");
  }

  /// <summary>
  /// Test that generator ignores commands without RestRoute (e.g., GraphQL only).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutRestRoute_DoesNotGenerateAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(GraphQLMutation = "createOrder")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNull();
  }

  /// <summary>
  /// Test that generator ignores classes without [CommandEndpoint] attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutCommandEndpointAttribute_DoesNotGenerateAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestApp;

            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNull();
  }

  /// <summary>
  /// Test that generator uses custom route from attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomRoute_UsesRouteFromAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/v2/custom-orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("/api/v2/custom-orders");
  }

  /// <summary>
  /// Test that generator produces compilable code with no errors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ProducesCompilableCodeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert - no compilation errors in generator itself
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    await Assert.That(errors).IsEmpty();
  }

  /// <summary>
  /// Test that generator uses correct namespace based on consumer assembly.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_UsesConsumerNamespaceAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace MyCompany.OrderService;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("namespace MyCompany.OrderService.Generated");
  }

  /// <summary>
  /// Test that generator generates diagnostics class.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesDiagnosticsClassAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("WhizbangRestMutationDiagnostics");
    await Assert.That(code).Contains("DiscoveredMutationCount");
  }

  /// <summary>
  /// Test that generator generates endpoint with POST method.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesPostEndpointAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Post(");
  }

  /// <summary>
  /// Test that generator generates partial class for extensibility.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesPartialClassAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("public partial class");
  }

  /// <summary>
  /// Test that generator inherits from RestMutationEndpointBase.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_InheritsFromRestMutationEndpointBaseAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(RestRoute = "/api/orders")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RestMutationEndpointBase<");
  }

  /// <summary>
  /// Test that generator handles both REST and GraphQL specified.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithBothRestAndGraphQL_GeneratesRestEndpointAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(
                RestRoute = "/api/orders",
                GraphQLMutation = "createOrder")]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrderCommandEndpoint");
    await Assert.That(code).Contains("/api/orders");
  }

  /// <summary>
  /// Test that generator handles custom RequestType.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomRequestType_GeneratesWithRequestTypeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }
            public class CreateOrderRequest { public string CustomerEmail { get; set; } }

            [CommandEndpoint<CreateOrderCommand, OrderResult>(
                RestRoute = "/api/orders",
                RequestType = typeof(CreateOrderRequest))]
            public class CreateOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    // When custom request type is specified, endpoint should use it
    await Assert.That(code!).Contains("CreateOrderRequest");
  }

  /// <summary>
  /// Test that endpoint name is derived from command name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DerivesEndpointNameFromCommandAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Transports.Mutations;

            namespace TestApp;

            public class OrderResult { public Guid Id { get; set; } }

            [CommandEndpoint<PlaceOrderCommand, OrderResult>(RestRoute = "/api/orders/place")]
            public class PlaceOrderCommand : ICommand { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestMutationEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    // Endpoint name should be PlaceOrderCommandEndpoint
    await Assert.That(code!).Contains("PlaceOrderCommandEndpoint");
  }
}
