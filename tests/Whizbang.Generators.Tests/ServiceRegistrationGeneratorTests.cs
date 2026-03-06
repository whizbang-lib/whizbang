using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ServiceRegistrationGenerator source generator.
/// Validates discovery of user interfaces extending Whizbang interfaces
/// and generation of DI service registrations.
/// </summary>
/// <tests>Whizbang.Generators/ServiceRegistrationGenerator.cs</tests>
[Category("Generators")]
[Category("DependencyInjection")]
public class ServiceRegistrationGeneratorTests {

  /// <summary>
  /// Helper to count occurrences of a string in text.
  /// </summary>
  private static int _countOccurrences(string text, string pattern) {
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1) {
      count++;
      index += pattern.Length;
    }
    return count;
  }

  // ===========================================
  // LENS SERVICE REGISTRATION TESTS
  // ===========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_UserLensInterface_RegistersInterfaceToImplementationAsync() {
    // Arrange - User interface extending ILensQuery, with implementation
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);

            public interface IOrderLens : ILensQuery<Order> {
              Task<Order?> GetByStatusAsync(string status, CancellationToken ct = default);
            }

            public class OrderLens : IOrderLens {
              private readonly ILensQuery<Order> _query;
              public OrderLens(ILensQuery<Order> query) => _query = query;
              public IQueryable<PerspectiveRow<Order>> Query => _query.Query;
              public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
              public Task<Order?> GetByStatusAsync(string status, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("AddLensServices");
    await Assert.That(code).Contains("AddTransient<global::TestApp.IOrderLens, global::TestApp.OrderLens>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SelfRegistration_EnabledByDefault_RegistersBothAsync() {
    // Arrange
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }
            public class OrderLens : IOrderLens {
              public IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
              public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    // Should register interface → implementation
    await Assert.That(code!).Contains("AddTransient<global::TestApp.IOrderLens, global::TestApp.OrderLens>");
    // Should also register self (default behavior)
    await Assert.That(code).Contains("AddTransient<global::TestApp.OrderLens>");
    // Verify options class is generated
    await Assert.That(code).Contains("ServiceRegistrationOptions");
    await Assert.That(code).Contains("IncludeSelfRegistration");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractLens_SkipsRegistrationAsync() {
    // Arrange - Abstract class should be skipped
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }

            public abstract class BaseLens : IOrderLens {
              public abstract IQueryable<PerspectiveRow<Order>> Query { get; }
              public abstract Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    // Should NOT contain registration for abstract class
    await Assert.That(code!).DoesNotContain("BaseLens");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractBaseWithConcreteChild_RegistersOnlyChildAsync() {
    // Arrange - Abstract base, concrete child
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }

            public abstract class BaseLens : IOrderLens {
              public abstract IQueryable<PerspectiveRow<Order>> Query { get; }
              public abstract Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
            }

            public class OrderLens : BaseLens {
              public override IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
              public override Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    // Should register concrete child
    await Assert.That(code!).Contains("OrderLens");
    // But NOT the abstract base
    await Assert.That(code).DoesNotContain("BaseLens");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DirectWhizbangImplementation_RegistersAgainstWhizbangInterfaceAsync() {
    // Arrange - Class directly implementing ILensQuery<T> (not through user interface)
    // Should be registered against the ILensQuery<T> interface
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);

            // Direct implementation - should be registered against ILensQuery<Order>
            public class DirectLensQuery : ILensQuery<Order> {
              public IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
              public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    // Should register against ILensQuery<Order>
    await Assert.That(code!).Contains("AddTransient<global::Whizbang.Core.Lenses.ILensQuery<global::TestApp.Order>, global::TestApp.DirectLensQuery>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleLenses_RegistersAllAsync() {
    // Arrange - Multiple user interfaces and implementations
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public record Product(Guid Id, string Name);
            public record Customer(Guid Id, string Email);

            public interface IOrderLens : ILensQuery<Order> { }
            public interface IProductLens : ILensQuery<Product> { }
            public interface ICustomerLens : ILensQuery<Customer> { }

            public class OrderLens : IOrderLens {
              public IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
              public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }

            public class ProductLens : IProductLens {
              public IQueryable<PerspectiveRow<Product>> Query => throw new NotImplementedException();
              public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Product?>(null);
            }

            public class CustomerLens : ICustomerLens {
              public IQueryable<PerspectiveRow<Customer>> Query => throw new NotImplementedException();
              public Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Customer?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IOrderLens, global::TestApp.OrderLens");
    await Assert.That(code).Contains("IProductLens, global::TestApp.ProductLens");
    await Assert.That(code).Contains("ICustomerLens, global::TestApp.CustomerLens");
  }

  // ===========================================
  // PERSPECTIVE SERVICE REGISTRATION TESTS
  // ===========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_UserPerspectiveInterface_RegistersInterfaceToImplementationAsync() {
    // Arrange - User interface extending IPerspectiveFor<>
    var source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public record OrderCreatedEvent : IEvent {
              public string OrderId { get; init; } = "";
            }

            public record OrderModel {
              public string OrderId { get; set; } = "";
            }

            public interface IOrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> { }

            public class OrderPerspective : IOrderPerspective {
              public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
                return currentData with { OrderId = @event.OrderId };
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("AddPerspectiveServices");
    await Assert.That(code).Contains("AddTransient<global::TestApp.IOrderPerspective, global::TestApp.OrderPerspective>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractPerspective_SkipsRegistrationAsync() {
    // Arrange - Abstract perspective class should be skipped
    var source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public record OrderCreatedEvent : IEvent {
              public string OrderId { get; init; } = "";
            }

            public record OrderModel {
              public string OrderId { get; set; } = "";
            }

            public interface IOrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> { }

            public abstract class BasePerspective : IOrderPerspective {
              public abstract OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("BasePerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DirectPerspectiveImplementation_RegistersAgainstWhizbangInterfaceAsync() {
    // Arrange - Class directly implementing IPerspectiveFor<> (no user interface)
    // Should be registered against the IPerspectiveFor<,> interface
    var source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public record OrderCreatedEvent : IEvent {
              public string OrderId { get; init; } = "";
            }

            public record OrderModel {
              public string OrderId { get; set; } = "";
            }

            // Direct implementation - should be registered against IPerspectiveFor<OrderModel, OrderCreatedEvent>
            public class DirectPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
              public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
                return currentData;
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    // Should register against IPerspectiveFor<OrderModel, OrderCreatedEvent>
    await Assert.That(code!).Contains("AddTransient<global::Whizbang.Core.Perspectives.IPerspectiveFor<global::TestApp.OrderModel, global::TestApp.OrderCreatedEvent>, global::TestApp.DirectPerspective>");
  }

  // ===========================================
  // COMBINED TESTS
  // ===========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CombinedLensAndPerspective_GeneratesBothMethodsAsync() {
    // Arrange - Both lens and perspective with user interfaces
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record OrderCreatedEvent : IEvent {
              public string OrderId { get; init; } = "";
            }

            public record OrderModel {
              public Guid Id { get; set; }
              public string OrderId { get; set; } = "";
            }

            // Perspective
            public interface IOrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> { }
            public class OrderPerspective : IOrderPerspective {
              public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
            }

            // Lens
            public interface IOrderLens : ILensQuery<OrderModel> { }
            public class OrderLens : IOrderLens {
              public IQueryable<PerspectiveRow<OrderModel>> Query => throw new NotImplementedException();
              public Task<OrderModel?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<OrderModel?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("AddPerspectiveServices");
    await Assert.That(code).Contains("AddLensServices");
    await Assert.That(code).Contains("AddAllWhizbangServices");
    await Assert.That(code).Contains("IOrderPerspective, global::TestApp.OrderPerspective");
    await Assert.That(code).Contains("IOrderLens, global::TestApp.OrderLens");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoUserInterfaces_GeneratesEmptyMethodsAsync() {
    // Arrange - No user interfaces
    var source = """
            using System;

            namespace TestApp;

            public class SomeClass {
              public void SomeMethod() { }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    // Should still generate methods (empty), or nothing at all
    // Either is acceptable - test should not fail
    if (code is not null) {
      await Assert.That(code).Contains("AddPerspectiveServices");
      await Assert.That(code).Contains("AddLensServices");
      // Should not contain any actual service registrations
      // Note: "AddScoped" may appear in XML doc comments, so we check for actual registration pattern
      await Assert.That(code).DoesNotContain("AddTransient<global::");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedUserInterface_RegistersWithFullNameAsync() {
    // Arrange - Nested class implementing user interface
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }

            public class OuterClass {
              public class NestedOrderLens : IOrderLens {
                public IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
                public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    // Should use full name for nested class
    await Assert.That(code!).Contains("OuterClass.NestedOrderLens");
  }

  // ===========================================
  // OPTIONS CLASS TESTS
  // ===========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_OptionsClass_GeneratedCorrectlyAsync() {
    // Arrange
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }
            public class OrderLens : IOrderLens {
              public IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
              public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("public sealed class ServiceRegistrationOptions");
    await Assert.That(code).Contains("public bool IncludeSelfRegistration");
    await Assert.That(code).Contains("= true"); // Default value
  }

  // ===========================================
  // DIAGNOSTIC TESTS
  // ===========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsInfoDiagnostic_WhenServiceDiscoveredAsync() {
    // Arrange
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }
            public class OrderLens : IOrderLens {
              public IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
              public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert - Should have WHIZ040 info diagnostic
    var diagnostics = result.Diagnostics;
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ040")).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsInfoDiagnostic_WhenAbstractClassSkippedAsync() {
    // Arrange
    var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }

            public abstract class AbstractOrderLens : IOrderLens {
              public abstract IQueryable<PerspectiveRow<Order>> Query { get; }
              public abstract Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    // Assert - Should have WHIZ041 info diagnostic
    var diagnostics = result.Diagnostics;
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ041")).IsTrue();
  }
}
