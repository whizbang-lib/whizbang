using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Whizbang.Transports.FastEndpoints.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for RestLensEndpointGenerator.
/// Validates discovery of [RestLens] attributes and generation of FastEndpoints endpoint classes.
/// </summary>
/// <tests>Whizbang.Transports.FastEndpoints.Generators/RestLensEndpointGenerator.cs</tests>
[Category("Generators")]
[Category("FastEndpoints")]
public class RestLensEndpointGeneratorTests {

  /// <summary>
  /// Test that generator discovers lens interface with [RestLens] and generates endpoint.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithRestLensAttribute_GeneratesEndpointAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderLensEndpoint");
    await Assert.That(code).Contains("IOrderLens");
  }

  /// <summary>
  /// Test that generator handles multiple lens types with [RestLens].
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleLenses_GeneratesAllEndpointsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);
            public record ProductReadModel(Guid Id, string Name);
            public record CustomerReadModel(Guid Id, string Email);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }

            [RestLens(Route = "/api/products")]
            public interface IProductLens : ILensQuery<ProductReadModel> { }

            [RestLens(Route = "/api/customers")]
            public interface ICustomerLens : ILensQuery<CustomerReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderLensEndpoint");
    await Assert.That(code).Contains("ProductLensEndpoint");
    await Assert.That(code).Contains("CustomerLensEndpoint");
  }

  /// <summary>
  /// Test that generator ignores lens without [RestLens] attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutRestLensAttribute_DoesNotGenerateAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
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
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/v2/custom-orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("/api/v2/custom-orders");
  }

  /// <summary>
  /// Test that generator uses custom page size from attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomPageSize_UsesPageSizeFromAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders", DefaultPageSize = 25, MaxPageSize = 200)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("25"); // DefaultPageSize
    await Assert.That(code).Contains("200"); // MaxPageSize
  }

  /// <summary>
  /// Test that generator generates default route from model type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutExplicitRoute_UsesDefaultRouteAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    // Should derive "/api/orders" from "OrderReadModel"
    await Assert.That(code!).Contains("/api/orders");
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
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert - no compilation errors in generator itself
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    await Assert.That(errors).IsEmpty();
  }

  /// <summary>
  /// Test that generator handles class with [RestLens] (not just interface).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithClassHavingRestLens_GeneratesEndpointAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record ProductReadModel(Guid Id, string Name);

            [RestLens(Route = "/api/products")]
            public class ProductLens : ILensQuery<ProductReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("ProductLensEndpoint");
    await Assert.That(code).Contains("ProductLens");
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
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace MyCompany.OrderService;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
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
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("WhizbangRestLensDiagnostics");
    await Assert.That(code).Contains("DiscoveredLensCount");
  }

  /// <summary>
  /// Test that generator generates endpoint with GET method.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesGetEndpointAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Get(");
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
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [RestLens(Route = "/api/orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("public partial class");
  }

  /// <summary>
  /// Test that default route is properly derived from Model suffix.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DerivesRouteFromModelSuffixAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record CustomerModel(Guid Id, string Name);

            [RestLens]
            public interface ICustomerLens : ILensQuery<CustomerModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    // Should derive "/api/customers" from "CustomerModel"
    await Assert.That(code!).Contains("/api/customers");
  }

  /// <summary>
  /// Test that default route is properly derived from Dto suffix.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DerivesRouteFromDtoSuffixAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.FastEndpoints;

            namespace TestApp;

            public record InvoiceDto(Guid Id, decimal Amount);

            [RestLens]
            public interface IInvoiceLens : ILensQuery<InvoiceDto> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangRestLensEndpoints.g.cs");
    await Assert.That(code).IsNotNull();
    // Should derive "/api/invoices" from "InvoiceDto"
    await Assert.That(code!).Contains("/api/invoices");
  }
}
