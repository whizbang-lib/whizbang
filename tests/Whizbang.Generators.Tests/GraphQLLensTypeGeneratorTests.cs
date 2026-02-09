using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Whizbang.Transports.HotChocolate.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for GraphQLLensTypeGenerator.
/// Validates discovery of [GraphQLLens] attributes and generation of HotChocolate query types.
/// </summary>
/// <tests>Whizbang.Transports.HotChocolate.Generators/GraphQLLensTypeGenerator.cs</tests>
[Category("Generators")]
[Category("GraphQL")]
public class GraphQLLensTypeGeneratorTests {

  /// <summary>
  /// Test that generator discovers lens interface with [GraphQLLens] and generates query type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithGraphQLLensAttribute_GeneratesQueryTypeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GetOrders");
    await Assert.That(code).Contains("IOrderLens");
  }

  /// <summary>
  /// Test that generator handles multiple lens types with [GraphQLLens].
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleLenses_GeneratesAllQueriesAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);
            public record ProductReadModel(Guid Id, string Name);
            public record CustomerReadModel(Guid Id, string Email);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }

            [GraphQLLens(QueryName = "products")]
            public interface IProductLens : ILensQuery<ProductReadModel> { }

            [GraphQLLens(QueryName = "customers")]
            public interface ICustomerLens : ILensQuery<CustomerReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GetOrders");
    await Assert.That(code).Contains("GetProducts");
    await Assert.That(code).Contains("GetCustomers");
  }

  /// <summary>
  /// Test that generator ignores lens without [GraphQLLens] attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutGraphQLLensAttribute_DoesNotGenerateAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNull();
  }

  /// <summary>
  /// Test that generator generates paging attributes by default.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithPagingEnabled_IncludesUsePagingAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders", EnablePaging = true, DefaultPageSize = 20, MaxPageSize = 50)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[UsePaging(DefaultPageSize = 20, MaxPageSize = 50)]");
  }

  /// <summary>
  /// Test that generator excludes paging when disabled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithPagingDisabled_ExcludesUsePagingAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders", EnablePaging = false)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("[UsePaging");
  }

  /// <summary>
  /// Test that generator includes filtering attributes when enabled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithFilteringEnabled_IncludesUseFilteringAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders", EnableFiltering = true)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[UseFiltering]");
  }

  /// <summary>
  /// Test that generator excludes filtering when disabled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithFilteringDisabled_ExcludesUseFilteringAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders", EnableFiltering = false)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("[UseFiltering]");
  }

  /// <summary>
  /// Test that generator includes sorting attributes when enabled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithSortingEnabled_IncludesUseSortingAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders", EnableSorting = true)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[UseSorting]");
  }

  /// <summary>
  /// Test that generator generates default query name from model type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutExplicitQueryName_UsesModelNameAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    // Should derive "orders" from "OrderReadModel" (remove ReadModel, pluralize, lowercase)
    await Assert.That(code!).Contains("GetOrders");
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
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert - no compilation errors in generator itself
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    await Assert.That(errors).IsEmpty();
  }

  /// <summary>
  /// Test that generator includes projection attributes when enabled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithProjectionEnabled_IncludesUseProjectionAttributeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders", EnableProjection = true)]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[UseProjection]");
  }

  /// <summary>
  /// Test that generator generates extension class for registration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesRegistrationExtensionsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("AddWhizbangLensQueries");
    await Assert.That(code).Contains("IRequestExecutorBuilder");
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
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("WhizbangGraphQLDiagnostics");
    await Assert.That(code).Contains("DiscoveredLensCount");
  }

  /// <summary>
  /// Test that generator handles class with [GraphQLLens] (not just interface).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithClassHavingGraphQLLens_GeneratesQueryTypeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record ProductReadModel(Guid Id, string Name);

            [GraphQLLens(QueryName = "products")]
            public class ProductLens : ILensQuery<ProductReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GetProducts");
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
            using Whizbang.Transports.HotChocolate;

            namespace MyCompany.OrderService;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("namespace MyCompany.OrderService.Generated");
  }

  /// <summary>
  /// Test that generator generates lens info properties for each discovered lens.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesLensInfoPropertiesAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record OrderReadModel(Guid Id, string Status);

            [GraphQLLens(QueryName = "orders")]
            public interface IOrderLens : ILensQuery<OrderReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrdersInfo");
  }

  /// <summary>
  /// Test that default query name is properly derived from Model suffix.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DerivesQueryNameFromModelSuffixAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record CustomerModel(Guid Id, string Name);

            [GraphQLLens]
            public interface ICustomerLens : ILensQuery<CustomerModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    // Should derive "customers" from "CustomerModel" (remove Model, pluralize, lowercase)
    await Assert.That(code!).Contains("GetCustomers");
  }

  /// <summary>
  /// Test that default query name is properly derived from Dto suffix.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DerivesQueryNameFromDtoSuffixAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record InvoiceDto(Guid Id, decimal Amount);

            [GraphQLLens]
            public interface IInvoiceLens : ILensQuery<InvoiceDto> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    // Should derive "invoices" from "InvoiceDto" (remove Dto, pluralize, lowercase)
    await Assert.That(code!).Contains("GetInvoices");
  }

  /// <summary>
  /// Test that generator respects all attribute configuration options at once.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAllOptions_GeneratesCorrectlyAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record AuditLogReadModel(Guid Id, string Action);

            [GraphQLLens(
                QueryName = "auditLogs",
                EnableFiltering = true,
                EnableSorting = true,
                EnablePaging = true,
                EnableProjection = true,
                DefaultPageSize = 25,
                MaxPageSize = 100)]
            public interface IAuditLens : ILensQuery<AuditLogReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GetAuditLogs");
    await Assert.That(code).Contains("[UseFiltering]");
    await Assert.That(code).Contains("[UseSorting]");
    await Assert.That(code).Contains("[UseProjection]");
    await Assert.That(code).Contains("[UsePaging(DefaultPageSize = 25, MaxPageSize = 100)]");
  }

  /// <summary>
  /// Test that generator generates minimal attributes when all features disabled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAllFeaturesDisabled_GeneratesMinimalAttributesAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;
            using Whizbang.Transports.HotChocolate;

            namespace TestApp;

            public record SimpleReadModel(Guid Id);

            [GraphQLLens(
                QueryName = "simple",
                EnableFiltering = false,
                EnableSorting = false,
                EnablePaging = false,
                EnableProjection = false)]
            public interface ISimpleLens : ILensQuery<SimpleReadModel> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangLensQueries.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GetSimple");
    await Assert.That(code).DoesNotContain("[UseFiltering]");
    await Assert.That(code).DoesNotContain("[UseSorting]");
    await Assert.That(code).DoesNotContain("[UsePaging");
    await Assert.That(code).DoesNotContain("[UseProjection]");
  }
}
