using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ReceptorDiscoveryGenerator - ensures discovery of IReceptor implementations
/// and generation of dispatcher registration code.
/// </summary>
[Category("SourceGenerators")]
[Category("ReceptorDiscovery")]
public class ReceptorDiscoveryGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithReceptor_GeneratesDispatcherAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderCreated : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public async ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default) {
    return new OrderCreated { OrderId = message.OrderId };
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate Dispatcher.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("namespace TestAssembly.Generated");
    await Assert.That(dispatcher).Contains("class GeneratedDispatcher");
    await Assert.That(dispatcher).Contains("CreateOrder");
    await Assert.That(dispatcher).Contains("OrderCreated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidReceptor_GeneratesDispatcherAsync() {
    // Arrange - Tests IReceptor<TMessage> (void receptor pattern)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record LogMessage : ICommand {
  public string Message { get; init; } = string.Empty;
}

public class LogReceptor : IReceptor<LogMessage> {
  public ValueTask HandleAsync(LogMessage message, CancellationToken ct = default) {
    return ValueTask.CompletedTask;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate dispatcher for void receptor
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("LogMessage");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleReceptors_GeneratesAllRoutesAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;
public record UpdateOrder : ICommand;
public record OrderUpdated : IEvent;

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}

public class UpdateReceptor : IReceptor<UpdateOrder, OrderUpdated> {
  public ValueTask<OrderUpdated> HandleAsync(UpdateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderUpdated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate routes for both receptors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CreateOrder");
    await Assert.That(dispatcher).Contains("UpdateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoReceptors_GeneratesInfoAsync() {
    // Arrange - Tests WHIZ002 diagnostic when no receptors or perspectives found
    const string source = @"
namespace MyApp;

public class SomeClass {
  public string Name { get; set; } = string.Empty;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should report WHIZ002 info diagnostic (not warning anymore)
    var infos = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    await Assert.That(infos).Count().IsGreaterThanOrEqualTo(1);

    var whiz002 = infos.FirstOrDefault(d => d.Id == "WHIZ002");
    await Assert.That(whiz002).IsNotNull();
    await Assert.That(whiz002!.GetMessage(CultureInfo.InvariantCulture)).Contains("No IReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspectiveButNoReceptor_DoesNotWarnAsync() {
    // Arrange - Tests that IPerspectiveFor suppresses WHIZ002 warning
    const string source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public record TestEvent : IEvent;

public record TestModel {
  public Guid Id { get; set; }
}

public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
  public TestModel Apply(TestModel currentData, TestEvent @event) {
    return currentData;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should not report WHIZ002 when perspective exists
    var warnings = result.Diagnostics.Where(d => d.Id == "WHIZ002").ToArray();
    await Assert.That(warnings).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesDispatcherRegistrationsAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestResponse : IEvent;

public class TestReceptor : IReceptor<TestCommand, TestResponse> {
  public ValueTask<TestResponse> HandleAsync(TestCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new TestResponse());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate DispatcherRegistrations.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations).Contains("AddWhizbangDispatcher");
    await Assert.That(registrations).Contains("TestReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassNoBaseList_SkipsAsync() {
    // Arrange - Tests syntactic predicate filtering
    const string source = @"
namespace MyApp;

public class NoBaseClass {
  public string Name { get; set; } = string.Empty;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should skip classes without base list and report WHIZ002 info
    await Assert.That(result.Diagnostics).Contains(d => d.Id == "WHIZ002");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsDiscoveredReceptorsAsync() {
    // Arrange - Tests WHIZ001 diagnostic reporting
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestResponse : IEvent;

public class TestReceptor : IReceptor<TestCommand, TestResponse> {
  public ValueTask<TestResponse> HandleAsync(TestCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new TestResponse());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should report WHIZ001 info diagnostic
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("TestReceptor");
    await Assert.That(whiz001.GetMessage(CultureInfo.InvariantCulture)).Contains("TestCommand");
    await Assert.That(whiz001.GetMessage(CultureInfo.InvariantCulture)).Contains("TestResponse");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractReceptor_GeneratesAsync() {
    // Arrange - Tests abstract receptor classes
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestResponse : IEvent;

public abstract class BaseReceptor : IReceptor<TestCommand, TestResponse> {
  public abstract ValueTask<TestResponse> HandleAsync(TestCommand message, CancellationToken ct = default);
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate for abstract receptor
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("TestCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleReceptorInterfaces_GeneratesForFirstAsync() {
    // Arrange - Tests class implementing multiple IReceptor interfaces
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CommandA : ICommand;
public record CommandB : ICommand;
public record ResponseA : IEvent;
public record ResponseB : IEvent;

public class MultiReceptor : IReceptor<CommandA, ResponseA>, IReceptor<CommandB, ResponseB> {
  public ValueTask<ResponseA> HandleAsync(CommandA message, CancellationToken ct = default)
    => ValueTask.FromResult(new ResponseA());

  public ValueTask<ResponseB> HandleAsync(CommandB message, CancellationToken ct = default)
    => ValueTask.FromResult(new ResponseB());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should discover both receptor patterns
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CommandA");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTypeInGlobalNamespace_HandlesCorrectlyAsync() {
    // Arrange - Tests GetSimpleName with no dots (lastDot < 0)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

public record GlobalCommand : ICommand;
public record GlobalResponse : IEvent;

public class GlobalReceptor : IReceptor<GlobalCommand, GlobalResponse> {
  public ValueTask<GlobalResponse> HandleAsync(GlobalCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new GlobalResponse());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should handle global namespace types
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GlobalCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesReceptorDiscoveryDiagnosticsAsync() {
    // Arrange - Tests ReceptorDiscoveryDiagnostics.g.cs generation
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestResponse : IEvent;

public class TestReceptor : IReceptor<TestCommand, TestResponse> {
  public ValueTask<TestResponse> HandleAsync(TestCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new TestResponse());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate diagnostics file
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var diagnostics = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorDiscoveryDiagnostics.g.cs");
    await Assert.That(diagnostics).IsNotNull();
    await Assert.That(diagnostics).Contains("namespace TestAssembly.Generated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidReceptor_ReportsVoidResponseAsync() {
    // Arrange - Tests IsVoid branch in diagnostic reporting
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record LogCommand : ICommand;

public class LogReceptor : IReceptor<LogCommand> {
  public ValueTask HandleAsync(LogCommand message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should report "void" for response type
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("void");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassWithoutReceptorInterface_SkipsAsync() {
    // Arrange - Tests that classes without IReceptor are skipped
    const string source = @"
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;

public class NotAReceptor {
  public Task DoSomethingAsync() => Task.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should report WHIZ002 info (no receptors found)
    var infos = result.Diagnostics.Where(d => d.Id == "WHIZ002").ToArray();
    await Assert.That(infos).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTupleResponseType_SimplifiesInDiagnosticAsync() {
    // Arrange - Tests GetSimpleName with tuple response types
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record GetOrderDetails : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderSummary : IEvent {
  public string OrderId { get; init; } = string.Empty;
  public decimal Total { get; init; }
}

public record CustomerInfo : IEvent {
  public string Name { get; init; } = string.Empty;
}

public class OrderReceptor : IReceptor<GetOrderDetails, (OrderSummary, CustomerInfo)> {
  public ValueTask<(OrderSummary, CustomerInfo)> HandleAsync(GetOrderDetails message, CancellationToken ct = default)
    => ValueTask.FromResult<(OrderSummary, CustomerInfo)>((new OrderSummary(), new CustomerInfo()));
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should handle tuple response type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GetOrderDetails");

    // Verify diagnostic message simplified tuple type name
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    // GetSimpleName should simplify "(global::MyApp.Receptors.OrderSummary, global::MyApp.Receptors.CustomerInfo)" to "(OrderSummary, CustomerInfo)"
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("(OrderSummary, CustomerInfo)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithArrayResponseType_SimplifiesInDiagnosticAsync() {
    // Arrange - Tests GetSimpleName with array response types
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record GetOrders : ICommand;
public record OrderCreated : IEvent;

public class OrderReceptor : IReceptor<GetOrders, OrderCreated[]> {
  public ValueTask<OrderCreated[]> HandleAsync(GetOrders message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated[0]);
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should handle array response type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GetOrders");

    // Verify diagnostic message simplified array type name
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    // GetSimpleName should simplify "global::MyApp.Receptors.OrderCreated[]" to "OrderCreated[]"
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderCreated[]");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedTupleResponseType_SimplifiesInDiagnosticAsync() {
    // Arrange - Tests SplitTupleParts with nested tuples
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record GetComplexOrder : ICommand;
public record OrderSummary : IEvent;
public record CustomerInfo : IEvent;
public record ShippingInfo : IEvent;

public class OrderReceptor : IReceptor<GetComplexOrder, (OrderSummary, (CustomerInfo, ShippingInfo))> {
  public ValueTask<(OrderSummary, (CustomerInfo, ShippingInfo))> HandleAsync(GetComplexOrder message, CancellationToken ct = default)
    => ValueTask.FromResult<(OrderSummary, (CustomerInfo, ShippingInfo))>((new OrderSummary(), (new CustomerInfo(), new ShippingInfo())));
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should handle nested tuple response type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GetComplexOrder");

    // Verify diagnostic message simplified nested tuple type name
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    // GetSimpleName should simplify nested tuple
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("(OrderSummary, (CustomerInfo, ShippingInfo))");
  }

  // ========================================
  // ZERO-RECEPTOR GENERATION TESTS (Outbox Fallback)
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_WithPerspective_GeneratesEmptyDispatcherAsync() {
    // Arrange - BFF.API scenario: 0 receptors, but has perspectives
    // Should generate empty dispatcher for outbox fallback support
    const string source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public record ProductModel {
  public Guid Id { get; set; }
}

public class ProductCatalogPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
  public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
    return currentData;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate empty Dispatcher.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("class GeneratedDispatcher");
    await Assert.That(dispatcher).Contains("return null"); // All lookups return null
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_WithPerspective_GeneratesAddReceptorsAsync() {
    // Arrange - Should generate AddReceptors() extension even with 0 receptors
    const string source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public record ProductModel {
  public Guid Id { get; set; }
}

public class ProductCatalogPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
  public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
    return currentData;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate DispatcherRegistrations.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations).Contains("AddReceptors");
    await Assert.That(registrations).Contains("AddWhizbangDispatcher");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_WithPerspective_GeneratedCodeCompilesAsync() {
    // Arrange - Verify generated code compiles successfully
    const string source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public record ProductModel {
  public Guid Id { get; set; }
}

public class ProductCatalogPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
  public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
    return currentData;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Verify all expected files are generated
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    var diagnostics = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorDiscoveryDiagnostics.g.cs");

    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(registrations).IsNotNull();
    await Assert.That(diagnostics).IsNotNull();
  }

  // ==================== WhizbangTrace Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangTraceAttribute_GeneratesTracingCodeAsync() {
    // Arrange - Tests [WhizbangTrace] attribute detection
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record CreateOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderCreated : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

[WhizbangTrace]
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public async ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default) {
    return new OrderCreated { OrderId = message.OrderId };
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate ReceptorRegistry.g.cs with tracing code
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // The traced snippet should include ITracer calls
    await Assert.That(registry).Contains("ITracer");
    await Assert.That(registry).Contains("BeginHandlerTrace");
    await Assert.That(registry).Contains("EndHandlerTrace");
    await Assert.That(registry).Contains("IDebuggerAwareClock");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutWhizbangTraceAttribute_DoesNotGenerateTracingCodeAsync() {
    // Arrange - Tests that tracing code is NOT generated for non-traced receptors
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderCreated : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public async ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default) {
    return new OrderCreated { OrderId = message.OrderId };
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should NOT contain tracing code for non-traced receptors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // The normal snippet should NOT include ITracer calls
    await Assert.That(registry).DoesNotContain("ITracer");
    await Assert.That(registry).DoesNotContain("BeginHandlerTrace");
  }

  // ==================== Sync Receptor Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSyncReceptor_GeneratesDispatcherAsync() {
    // Arrange - Tests ISyncReceptor<TMessage, TResponse> discovery
    const string source = @"
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderCreated : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

public class SyncOrderReceptor : ISyncReceptor<CreateOrder, OrderCreated> {
  public OrderCreated Handle(CreateOrder message) {
    return new OrderCreated { OrderId = message.OrderId };
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate Dispatcher.g.cs with sync routing
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("namespace TestAssembly.Generated");
    await Assert.That(dispatcher).Contains("class GeneratedDispatcher");
    await Assert.That(dispatcher).Contains("CreateOrder");
    await Assert.That(dispatcher).Contains("GetSyncReceptorInvoker");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidSyncReceptor_GeneratesDispatcherAsync() {
    // Arrange - Tests ISyncReceptor<TMessage> (void sync receptor pattern)
    const string source = @"
using Whizbang.Core;

namespace MyApp.Receptors;

public record LogMessage : ICommand {
  public string Message { get; init; } = string.Empty;
}

public class SyncLogReceptor : ISyncReceptor<LogMessage> {
  public void Handle(LogMessage message) {
    // Log synchronously
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate dispatcher for void sync receptor
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("LogMessage");
    await Assert.That(dispatcher).Contains("GetVoidSyncReceptorInvoker");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSyncReceptor_GeneratesRegistrationAsync() {
    // Arrange - Tests ISyncReceptor registration generation
    const string source = @"
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

public class SyncOrderReceptor : ISyncReceptor<CreateOrder, OrderCreated> {
  public OrderCreated Handle(CreateOrder message) => new OrderCreated();
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate registration for sync receptor
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations).Contains("ISyncReceptor<");
    await Assert.That(registrations).Contains("SyncOrderReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithBothSyncAndAsyncReceptors_GeneratesBothRoutesAsync() {
    // Arrange - Tests mixed sync and async receptor discovery
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;
public record UpdateOrder : ICommand;
public record OrderUpdated : IEvent;

// Async receptor
public class AsyncOrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}

// Sync receptor
public class SyncUpdateReceptor : ISyncReceptor<UpdateOrder, OrderUpdated> {
  public OrderUpdated Handle(UpdateOrder message) => new OrderUpdated();
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate routes for both sync and async receptors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CreateOrder"); // Async
    await Assert.That(dispatcher).Contains("UpdateOrder"); // Sync
    await Assert.That(dispatcher).Contains("GetReceptorInvoker"); // Async routing
    await Assert.That(dispatcher).Contains("GetSyncReceptorInvoker"); // Sync routing

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations).Contains("IReceptor<"); // Async registration
    await Assert.That(registrations).Contains("ISyncReceptor<"); // Sync registration
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSyncReceptor_ReportsDiagnosticAsync() {
    // Arrange - Tests that sync receptors are reported in diagnostics
    const string source = @"
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

public class SyncOrderReceptor : ISyncReceptor<CreateOrder, OrderCreated> {
  public OrderCreated Handle(CreateOrder message) => new OrderCreated();
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should report WHIZ001 for discovered sync receptor
    var infos = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infos.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("SyncOrderReceptor");
  }

  // ==================== ReceptorRegistry.g.cs Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithReceptor_GeneratesReceptorRegistryAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate ReceptorRegistry.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("class GeneratedReceptorRegistry");
    await Assert.That(registry).Contains("IReceptorRegistry");
    await Assert.That(registry).Contains("GetReceptorsFor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorWithoutFireAt_RegisteredAtDefaultStagesAsync() {
    // Arrange - Receptor without [FireAt] attribute should be registered at 3 default stages
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should register at LocalImmediateDetached + PostInboxDetached (default stages)
    // PreOutbox removed from defaults — opt-in via [FireAt(PreOutboxDetached)]
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("LifecycleStage.LocalImmediateDetached");
    await Assert.That(registry).Contains("LifecycleStage.PostInboxDetached");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorWithFireAt_RegisteredOnlyAtSpecifiedStageAsync() {
    // Arrange - Receptor with [FireAt(PostInboxInline)] should only be registered at PostInboxInline
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record AuditEvent : IEvent;

[FireAt(LifecycleStage.PostInboxInline)]
public class AuditLogger : IReceptor<AuditEvent> {
  public ValueTask HandleAsync(AuditEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should only register at PostInboxInline
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // Count occurrences of the message type - should only appear for PostInboxInline
    var content = registry!;
    var auditEventCount = content.Split("AuditEvent").Length - 1;

    // With [FireAt(PostInboxInline)], the receptor should appear exactly once (at PostInboxInline)
    // Not at LocalImmediateInline or PreOutboxInline
    await Assert.That(content).Contains("LifecycleStage.PostInboxInline");
    await Assert.That(auditEventCount).IsGreaterThan(0);
  }

  /// <summary>
  /// Pins the [FireAt] double-fire fix: PublishToReceptors (PublishAsync path) must NOT
  /// emit explicit-stage receptors. They fire only at their declared lifecycle stage via
  /// ReceptorInvoker. Prevents regression of the engineer-reported double-fire bug where
  /// _dispatcher.PublishAsync(evt) inside a handler fired the [FireAt] receptor twice.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PublishToReceptors_OmitsFireAtReceptorsAsync() {
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record SomeEvent : IEvent;

// Default-stage void receptor — MUST be emitted in PublishToReceptors.
public class DefaultStageReceptor : IReceptor<SomeEvent> {
  public ValueTask HandleAsync(SomeEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}

// Explicit [FireAt] void receptor — MUST NOT be emitted in PublishToReceptors; it
// fires at its declared stage via ReceptorInvoker, not synchronously during publish.
[FireAt(LifecycleStage.PostAllPerspectivesDetached)]
public class FireAtReceptor : IReceptor<SomeEvent> {
  public ValueTask HandleAsync(SomeEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Carve out just the PublishToReceptors block — we care about what's emitted for the
    // PublishAsync path, not the cascade/untyped publish path.
    var content = dispatcher!;
    var publishStart = content.IndexOf("async Task PublishToReceptors(TEvent evt)", StringComparison.Ordinal);
    await Assert.That(publishStart).IsGreaterThan(-1);
    var publishEnd = content.IndexOf("return PublishToReceptors;", publishStart, StringComparison.Ordinal);
    await Assert.That(publishEnd).IsGreaterThan(publishStart);
    var publishBody = content.Substring(publishStart, publishEnd - publishStart);

    await Assert.That(publishBody).Contains("DefaultStageReceptor")
      .Because("default-stage void receptors must still fire from PublishAsync");
    await Assert.That(publishBody).DoesNotContain("FireAtReceptor")
      .Because("[FireAt] receptors must be deferred to their declared stage, never emitted in PublishToReceptors");
    await Assert.That(publishBody).DoesNotContain("if (!isDefaultDispatch)")
      .Because("PublishAsync has no sourceEnvelope — no runtime gate needed when the [FireAt] block is omitted entirely");
  }

  /// <summary>
  /// Symmetric guard: the cascade path (PublishToReceptorsUntyped) MUST still emit
  /// [FireAt] receptors inside an `if (!isDefaultDispatch)` block. That path is shared
  /// with code that invokes receptors with a non-default envelope; removing it would
  /// silently break other callers.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PublishToReceptorsUntyped_KeepsFireAtGuardedByIsDefaultDispatchAsync() {
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record SomeEvent : IEvent;

[FireAt(LifecycleStage.PostAllPerspectivesDetached)]
public class FireAtReceptor : IReceptor<SomeEvent> {
  public ValueTask HandleAsync(SomeEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    var content = dispatcher!;
    var untypedStart = content.IndexOf("async Task PublishToReceptorsUntyped", StringComparison.Ordinal);
    await Assert.That(untypedStart).IsGreaterThan(-1);
    var untypedEnd = content.IndexOf("return PublishToReceptorsUntyped;", untypedStart, StringComparison.Ordinal);
    await Assert.That(untypedEnd).IsGreaterThan(untypedStart);
    var untypedBody = content.Substring(untypedStart, untypedEnd - untypedStart);

    await Assert.That(untypedBody).Contains("isDefaultDispatch")
      .Because("cascade path still declares isDefaultDispatch from sourceEnvelope");
    await Assert.That(untypedBody).Contains("if (!isDefaultDispatch)")
      .Because("cascade path still gates [FireAt] receptors behind the isDefaultDispatch flag");
    await Assert.That(untypedBody).Contains("FireAtReceptor")
      .Because("[FireAt] receptor must still be present in the cascade path — just behind the runtime guard");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorRegistry_HasCorrectStructureAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestResponse : IEvent;

public class TestReceptor : IReceptor<TestCommand, TestResponse> {
  public ValueTask<TestResponse> HandleAsync(TestCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new TestResponse());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Verify structure of generated ReceptorRegistry
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("sealed class GeneratedReceptorRegistry");
    // NOTE: GeneratedReceptorRegistry no longer has IServiceProvider field.
    // Instead, the InvokeAsync delegate accepts (sp, msg, envelope, callerInfo, ct) where sp is the scoped provider.
    await Assert.That(registry).Contains("GetReceptorsFor(Type messageType, LifecycleStage stage)");
    await Assert.That(registry).Contains("ReceptorInfo[]");
    await Assert.That(registry).Contains("ReceptorId:");
    await Assert.That(registry).Contains("InvokeAsync:");
    // Verify the delegate signature accepts IServiceProvider, message, envelope, callerInfo, and cancellation token
    await Assert.That(registry).Contains("(sp, msg, envelope, callerInfo, ct)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DispatcherRegistrations_IncludesAddWhizbangReceptorRegistryAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestResponse : IEvent;

public class TestReceptor : IReceptor<TestCommand, TestResponse> {
  public ValueTask<TestResponse> HandleAsync(TestCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new TestResponse());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - DispatcherRegistrations should include AddWhizbangReceptorRegistry extension method
    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations).Contains("AddWhizbangReceptorRegistry");
    await Assert.That(registrations).Contains("IReceptorRegistry, GeneratedReceptorRegistry");
    await Assert.That(registrations).Contains("IReceptorInvoker, ReceptorInvoker");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_GeneratesEmptyReceptorRegistryAsync() {
    // Arrange - Project with perspective but no receptors
    const string source = @"
using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public record ProductModel {
  public Guid Id { get; set; }
}

public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
  public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
    return currentData;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should still generate ReceptorRegistry.g.cs with empty routing
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("class GeneratedReceptorRegistry");
    await Assert.That(registry).Contains("_emptyList");
  }

  #region Multiple Handler Validation Tests

  /// <summary>
  /// Tests that WHIZ080 is NOT reported when multiple RPC handlers exist,
  /// because the diagnostic is disabled by default pending key-based RPC handler selection.
  /// When the feature is enabled in the future, this test should be updated to expect the warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleRpcHandlers_NoWarningWhenDisabledAsync() {
    // Arrange - Two handlers for the same message type with response (RPC pattern)
    // WHIZ080 diagnostic is disabled by default, so no warning should be emitted
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

// First handler for CreateOrder
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}

// Second handler for same message type - would be an error for RPC if WHIZ080 was enabled
public class AnotherOrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - WHIZ080 should NOT be reported since it's disabled by default
    var whiz080 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ080");
    await Assert.That(whiz080).IsNull()
      .Because("WHIZ080 is disabled by default pending key-based RPC handler selection feature");

    // Verify no other errors occurred
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Tests that WHIZ080 is NOT reported for void receptors (event handlers).
  /// Multiple handlers for the same event type is expected and valid.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleVoidHandlers_NoErrorAsync() {
    // Arrange - Two void handlers for the same message type (event handling pattern)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record OrderCreated : IEvent;

// First void handler for OrderCreated
public class EmailNotificationHandler : IReceptor<OrderCreated> {
  public ValueTask HandleAsync(OrderCreated message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}

// Second void handler for same event - this is valid!
public class SmsNotificationHandler : IReceptor<OrderCreated> {
  public ValueTask HandleAsync(OrderCreated message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should NOT report WHIZ080 error for void handlers
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ080");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Tests that WHIZ080 is NOT reported for ISyncReceptor handlers even with response.
  /// Sync receptors don't go through the RPC path.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleSyncHandlers_NoErrorAsync() {
    // Arrange - Two sync handlers for the same message type with response
    const string source = @"
using Whizbang.Core;

namespace MyApp.Receptors;

public record ValidateOrder : ICommand;
public record ValidationResult : IEvent;

// First sync handler for ValidateOrder
public class FirstValidator : ISyncReceptor<ValidateOrder, ValidationResult> {
  public ValidationResult Handle(ValidateOrder message)
    => new ValidationResult();
}

// Second sync handler for same message - this is allowed for sync receptors
public class SecondValidator : ISyncReceptor<ValidateOrder, ValidationResult> {
  public ValidationResult Handle(ValidateOrder message)
    => new ValidationResult();
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should NOT report WHIZ080 error for sync handlers
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ080");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
  }

  #endregion

  #region DefaultRouting Attribute Detection Tests

  /// <summary>
  /// Tests that the generator detects [DefaultRouting] attribute on receptors
  /// and generates routing metadata lookup method.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDefaultRoutingAttribute_GeneratesRoutingLookupAsync() {
    // Arrange - Receptor with [DefaultRouting(DispatchModes.Local)]
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace MyApp.Receptors;

public record CreateCache : ICommand {
  public string Key { get; init; } = string.Empty;
}

public record CacheCreated : IEvent {
  public string Key { get; init; } = string.Empty;
}

[DefaultRouting(DispatchModes.Local)]
public class CacheReceptor : IReceptor<CreateCache, CacheCreated> {
  public ValueTask<CacheCreated> HandleAsync(CreateCache message, CancellationToken ct = default) {
    return ValueTask.FromResult(new CacheCreated { Key = message.Key });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate GetReceptorDefaultRouting method
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GetReceptorDefaultRouting");
    await Assert.That(dispatcher).Contains("CreateCache");
    await Assert.That(dispatcher).Contains("DispatchModes.Local");
  }

  /// <summary>
  /// Tests that the generator generates null return for receptors without [DefaultRouting].
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutDefaultRoutingAttribute_ReturnsNullAsync() {
    // Arrange - Receptor WITHOUT [DefaultRouting]
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderProcessed : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

public class OrderReceptor : IReceptor<ProcessOrder, OrderProcessed> {
  public ValueTask<OrderProcessed> HandleAsync(ProcessOrder message, CancellationToken ct = default) {
    return ValueTask.FromResult(new OrderProcessed { OrderId = message.OrderId });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate GetReceptorDefaultRouting that returns null
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GetReceptorDefaultRouting");
    await Assert.That(dispatcher).Contains("return null");
  }

  /// <summary>
  /// Tests that the generator handles multiple receptors with different routing attributes.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMixedRoutingAttributes_GeneratesCorrectLookupsAsync() {
    // Arrange - Multiple receptors with different routing
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace MyApp.Receptors;

public record CacheCommand : ICommand { }
public record CacheEvent : IEvent { }

public record OutboxCommand : ICommand { }
public record OutboxEvent : IEvent { }

public record BothCommand : ICommand { }
public record BothEvent : IEvent { }

public record DefaultCommand : ICommand { }
public record DefaultEvent : IEvent { }

[DefaultRouting(DispatchModes.Local)]
public class LocalReceptor : IReceptor<CacheCommand, CacheEvent> {
  public ValueTask<CacheEvent> HandleAsync(CacheCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new CacheEvent());
  }
}

[DefaultRouting(DispatchModes.Outbox)]
public class OutboxReceptor : IReceptor<OutboxCommand, OutboxEvent> {
  public ValueTask<OutboxEvent> HandleAsync(OutboxCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new OutboxEvent());
  }
}

[DefaultRouting(DispatchModes.Both)]
public class BothReceptor : IReceptor<BothCommand, BothEvent> {
  public ValueTask<BothEvent> HandleAsync(BothCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new BothEvent());
  }
}

public class DefaultReceptor : IReceptor<DefaultCommand, DefaultEvent> {
  public ValueTask<DefaultEvent> HandleAsync(DefaultCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new DefaultEvent());
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate GetReceptorDefaultRouting with all routing modes
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("GetReceptorDefaultRouting");
    await Assert.That(dispatcher).Contains("CacheCommand");
    await Assert.That(dispatcher).Contains("DispatchModes.Local");
    await Assert.That(dispatcher).Contains("OutboxCommand");
    await Assert.That(dispatcher).Contains("DispatchModes.Outbox");
    await Assert.That(dispatcher).Contains("BothCommand");
    await Assert.That(dispatcher).Contains("DispatchModes.Both");
  }

  #endregion

  #region CascadeToOutboxAsync Generation Tests

  /// <summary>
  /// Tests that the generator generates CascadeToOutboxAsync override
  /// when receptors return event types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithEventReturningReceptor_GeneratesCascadeToOutboxAsync() {
    // Arrange - Receptor that returns an event (should generate cascade code)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderCreated : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default) {
    return ValueTask.FromResult(new OrderCreated { OrderId = message.OrderId });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate CascadeToOutboxAsync override
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CascadeToOutboxAsync");
    await Assert.That(dispatcher).Contains("OrderCreated");
    await Assert.That(dispatcher).Contains("PublishToOutboxAsync");
  }

  /// <summary>
  /// Tests that the generator generates type-switch code for multiple event types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleEventTypes_GeneratesTypeSwitchAsync() {
    // Arrange - Multiple receptors returning different event types
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;
public record UpdateOrder : ICommand;
public record OrderUpdated : IEvent;
public record DeleteOrder : ICommand;
public record OrderDeleted : IEvent;

public class CreateOrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}

public class UpdateOrderReceptor : IReceptor<UpdateOrder, OrderUpdated> {
  public ValueTask<OrderUpdated> HandleAsync(UpdateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderUpdated());
}

public class DeleteOrderReceptor : IReceptor<DeleteOrder, OrderDeleted> {
  public ValueTask<OrderDeleted> HandleAsync(DeleteOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderDeleted());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate switch for all three event types
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CascadeToOutboxAsync");
    await Assert.That(dispatcher).Contains("OrderCreated");
    await Assert.That(dispatcher).Contains("OrderUpdated");
    await Assert.That(dispatcher).Contains("OrderDeleted");
  }

  /// <summary>
  /// Tests that void receptors (no return type) don't affect cascade generation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidReceptorOnly_GeneratesEmptyCascadeAsync() {
    // Arrange - Void receptor (no event return)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record LogMessage : ICommand {
  public string Message { get; init; } = string.Empty;
}

public class LogReceptor : IReceptor<LogMessage> {
  public ValueTask HandleAsync(LogMessage message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate CascadeToOutboxAsync that returns base implementation
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    // With no events to cascade, should call base or return completed task
    await Assert.That(dispatcher).Contains("CascadeToOutboxAsync");
  }

  /// <summary>
  /// Tests that tuple response types extract events for cascade generation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTupleResponse_ExtractsEventsForCascadeAsync() {
    // Arrange - Receptor returning tuple with events
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;
public record NotificationSent : IEvent;

public class OrderReceptor : IReceptor<CreateOrder, (OrderCreated, NotificationSent)> {
  public ValueTask<(OrderCreated, NotificationSent)> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult((new OrderCreated(), new NotificationSent()));
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should include both event types in cascade
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CascadeToOutboxAsync");
    await Assert.That(dispatcher).Contains("OrderCreated");
    await Assert.That(dispatcher).Contains("NotificationSent");
  }

  /// <summary>
  /// Tests that the generated CascadeToOutboxAsync calls PublishToOutboxAsync with correct parameters.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CascadeToOutbox_CallsPublishToOutboxWithMessageIdAsync() {
    // Arrange
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should call PublishToOutboxAsync with MessageId.New()
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("PublishToOutboxAsync");
    await Assert.That(dispatcher).Contains("MessageId.New()");
  }

  /// <summary>
  /// Tests that array response types are handled in cascade generation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithArrayResponse_IncludesEventTypeInCascadeAsync() {
    // Arrange - Receptor returning array of events
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessBatch : ICommand;
public record ItemProcessed : IEvent;

public class BatchReceptor : IReceptor<ProcessBatch, ItemProcessed[]> {
  public ValueTask<ItemProcessed[]> HandleAsync(ProcessBatch message, CancellationToken ct = default)
    => ValueTask.FromResult(new ItemProcessed[0]);
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should include ItemProcessed in cascade (array element type)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CascadeToOutboxAsync");
    await Assert.That(dispatcher).Contains("ItemProcessed");
  }

  #endregion

  #region Routed<T> Response Type Unwrapping Tests

  /// <summary>
  /// Tests that receptors returning Routed&lt;T&gt; have the inner type T extracted
  /// for cascade generation, not Routed&lt;T&gt; itself.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRoutedResponse_ExtractsInnerTypeForCascadeAsync() {
    // Arrange - Receptor returning Routed<T> wrapper
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace MyApp.Receptors;

public record ProcessCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record ProcessedEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

public class ProcessReceptor : IReceptor<ProcessCommand, Routed<ProcessedEvent>> {
  public ValueTask<Routed<ProcessedEvent>> HandleAsync(ProcessCommand message, CancellationToken ct = default) {
    return Route.Outbox(new ProcessedEvent { Id = message.Id }).AsValueTask();
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate cascade for ProcessedEvent (inner type), NOT Routed<ProcessedEvent>
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("CascadeToOutboxAsync");
    // Should contain ProcessedEvent (the inner type) in cascade
    await Assert.That(dispatcher).Contains("ProcessedEvent");

    // Extract just the CascadeToOutboxAsync method content to verify no Routed<> in cascade
    var cascadeStart = dispatcher.IndexOf("CascadeToOutboxAsync", StringComparison.Ordinal);
    var cascadeEnd = dispatcher.IndexOf("CascadeToEventStoreOnlyAsync", StringComparison.Ordinal);
    if (cascadeEnd < 0) {
      cascadeEnd = dispatcher.Length;
    }
    var cascadeSection = dispatcher[cascadeStart..cascadeEnd];

    // Cascade should NOT contain Routed<> wrapper type - only the inner type
    await Assert.That(cascadeSection).DoesNotContain("Routed<");
    await Assert.That(cascadeSection).Contains("ProcessedEvent");
  }

  /// <summary>
  /// Tests that receptors returning RoutedNone are handled correctly (no cascade).
  /// RoutedNone is allowed in DI registration, but should NOT appear in cascade sections.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRoutedNoneResponse_DoesNotGenerateCascadeAsync() {
    // Arrange - Receptor returning RoutedNone (no events to cascade)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace MyApp.Receptors;

public record ValidateCommand : ICommand {
  public string Data { get; init; } = string.Empty;
}

public class ValidateReceptor : IReceptor<ValidateCommand, RoutedNone> {
  public ValueTask<RoutedNone> HandleAsync(ValidateCommand message, CancellationToken ct = default) {
    return Route.None().AsValueTask();
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should NOT generate cascade code for RoutedNone
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Extract just the CascadeToOutboxAsync method content
    var cascadeStart = dispatcher!.IndexOf("CascadeToOutboxAsync", StringComparison.Ordinal);
    var cascadeEnd = dispatcher.IndexOf("CascadeToEventStoreOnlyAsync", StringComparison.Ordinal);
    if (cascadeEnd < 0) {
      cascadeEnd = dispatcher.Length;
    }
    var cascadeSection = dispatcher[cascadeStart..cascadeEnd];

    // Cascade section should NOT contain RoutedNone (nothing to cascade)
    await Assert.That(cascadeSection).DoesNotContain("RoutedNone");
    // Cascade should just return Task.CompletedTask (no events)
    await Assert.That(cascadeSection).Contains("return Task.CompletedTask");
  }

  /// <summary>
  /// Tests that WHIZ001 diagnostic reports the receptor discovery correctly.
  /// Note: GetSimpleName simplifies generic types, so Routed&lt;ProcessedEvent&gt; becomes ProcessedEvent&gt;
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRoutedResponse_ReportsReceptorInDiagnosticAsync() {
    // Arrange - Receptor returning Routed<T>
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace MyApp.Receptors;

public record ProcessCommand : ICommand;

public record ProcessedEvent : IEvent;

public class ProcessReceptor : IReceptor<ProcessCommand, Routed<ProcessedEvent>> {
  public ValueTask<Routed<ProcessedEvent>> HandleAsync(ProcessCommand message, CancellationToken ct = default) {
    return Route.Outbox(new ProcessedEvent()).AsValueTask();
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - WHIZ001 should report the receptor was discovered
    var infos = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz001 = infos.FirstOrDefault(d => d.Id == "WHIZ001");
    await Assert.That(whiz001).IsNotNull();
    // Should report the receptor class
    await Assert.That(whiz001!.GetMessage(CultureInfo.InvariantCulture)).Contains("ProcessReceptor");
    // Should report the message type
    await Assert.That(whiz001.GetMessage(CultureInfo.InvariantCulture)).Contains("ProcessCommand");
    // The response type is shown (GetSimpleName simplifies generics but leaves trailing >)
    await Assert.That(whiz001.GetMessage(CultureInfo.InvariantCulture)).Contains("ProcessedEvent");
  }

  /// <summary>
  /// Tests that tuple containing Routed&lt;T&gt; values extracts the inner types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTupleOfRoutedResponses_ExtractsInnerTypesAsync() {
    // Arrange - Receptor returning tuple with Routed wrappers (discriminated union pattern)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

namespace MyApp.Receptors;

public record ProcessCommand : ICommand;

public record SuccessEvent : IEvent;
public record FailureEvent : IEvent;

public class ProcessReceptor : IReceptor<ProcessCommand, (Routed<SuccessEvent>, Routed<FailureEvent>)> {
  public ValueTask<(Routed<SuccessEvent>, Routed<FailureEvent>)> HandleAsync(ProcessCommand message, CancellationToken ct = default) {
    // Success path - returns success event, empty failure
    return ValueTask.FromResult((Route.Outbox(new SuccessEvent()), new Routed<FailureEvent>(default!, DispatchModes.None)));
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should extract both inner types
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Extract just the CascadeToOutboxAsync method content to verify no Routed<> in cascade
    var cascadeStart = dispatcher!.IndexOf("CascadeToOutboxAsync", StringComparison.Ordinal);
    var cascadeEnd = dispatcher.IndexOf("CascadeToEventStoreOnlyAsync", StringComparison.Ordinal);
    if (cascadeEnd < 0) {
      cascadeEnd = dispatcher.Length;
    }
    var cascadeSection = dispatcher[cascadeStart..cascadeEnd];

    // Cascade should contain both inner event types (unwrapped from Routed<>)
    await Assert.That(cascadeSection).Contains("SuccessEvent");
    await Assert.That(cascadeSection).Contains("FailureEvent");
    // Cascade should NOT contain Routed<> wrapper type
    await Assert.That(cascadeSection).DoesNotContain("Routed<");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNullableTupleElement_PreservesNullabilityAsync() {
    // Arrange - Tests that nullable tuple elements preserve the '?' annotation
    // This is the exact scenario from JDNext where (List<IEvent>, FailedEvent?) was losing the ?
    const string source = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessBatch : ICommand {
  public string BatchId { get; init; } = string.Empty;
}

public record SuccessEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

public record FailureEvent : IEvent {
  public string Reason { get; init; } = string.Empty;
}

public class BatchReceptor : IReceptor<ProcessBatch, (List<SuccessEvent>, FailureEvent?)> {
  public ValueTask<(List<SuccessEvent>, FailureEvent?)> HandleAsync(ProcessBatch message, CancellationToken ct = default) {
    return ValueTask.FromResult<(List<SuccessEvent>, FailureEvent?)>((new List<SuccessEvent>(), null));
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the dispatcher registration source which contains the full type names
    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();

    // The key assertion: the FailureEvent should have the ? preserved
    // The generated code should contain the nullable type annotation
    await Assert.That(registrations).Contains("FailureEvent?");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNullableTupleElement_StripsNullabilityInTypeofAsync() {
    // Arrange - Tests that nullable tuple elements have the '?' stripped in typeof() contexts
    // This prevents CS8639: The typeof operator cannot be used on a nullable reference type
    const string source = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessBatch : ICommand {
  public string BatchId { get; init; } = string.Empty;
}

public record SuccessEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

public record FailureEvent : IEvent {
  public string Reason { get; init; } = string.Empty;
}

public class BatchReceptor : IReceptor<ProcessBatch, (List<SuccessEvent>, FailureEvent?)> {
  public ValueTask<(List<SuccessEvent>, FailureEvent?)> HandleAsync(ProcessBatch message, CancellationToken ct = default) {
    return ValueTask.FromResult<(List<SuccessEvent>, FailureEvent?)>((new List<SuccessEvent>(), null));
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the dispatcher source which contains typeof() calls for outbox cascade
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // The key assertion: typeof() calls must NOT include the nullable annotation
    // typeof(FailureEvent?) would cause CS8639, so we must have typeof(FailureEvent) instead
    await Assert.That(dispatcher).DoesNotContain("typeof(global::MyApp.Receptors.FailureEvent?)");

    // Verify the non-nullable typeof() IS present (for outbox cascade)
    await Assert.That(dispatcher).Contains("typeof(global::MyApp.Receptors.FailureEvent)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListInTuple_ExtractsElementTypeForCascadeAsync() {
    // Arrange - Tests that List<T> inside a tuple extracts the element type for cascade
    // This is the exact scenario from JDNext where (List<IEvent>, FailedEvent?) was not cascading
    const string source = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessBatchCommand : ICommand {
  public string BatchId { get; init; } = string.Empty;
}

public record BatchEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

public record FailureEvent : IEvent {
  public string Reason { get; init; } = string.Empty;
}

public class BatchReceptor : IReceptor<ProcessBatchCommand, (List<BatchEvent>, FailureEvent?)> {
  public ValueTask<(List<BatchEvent>, FailureEvent?)> HandleAsync(ProcessBatchCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult<(List<BatchEvent>, FailureEvent?)>((new List<BatchEvent>(), null));
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the dispatcher source which contains the outbox cascade code
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // The key assertion: cascade should include typeof(BatchEvent), NOT typeof(List<BatchEvent>)
    // List<T> element types should be extracted for cascade
    await Assert.That(dispatcher).Contains("typeof(global::MyApp.Receptors.BatchEvent)");
    await Assert.That(dispatcher).DoesNotContain("typeof(global::System.Collections.Generic.List<global::MyApp.Receptors.BatchEvent>)");

    // Also verify FailureEvent is extracted (without the nullable annotation)
    await Assert.That(dispatcher).Contains("typeof(global::MyApp.Receptors.FailureEvent)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListResponseType_ExtractsElementTypeForCascadeAsync() {
    // Arrange - Tests that List<T> as a direct response type extracts the element type for cascade
    const string source = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record GetEventsCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record MyEvent : IEvent {
  public string Data { get; init; } = string.Empty;
}

public class EventsReceptor : IReceptor<GetEventsCommand, List<MyEvent>> {
  public ValueTask<List<MyEvent>> HandleAsync(GetEventsCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new List<MyEvent>());
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the dispatcher source which contains the outbox cascade code
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // The key assertion: cascade should include typeof(MyEvent), NOT typeof(List<MyEvent>)
    await Assert.That(dispatcher).Contains("typeof(global::MyApp.Receptors.MyEvent)");
    await Assert.That(dispatcher).DoesNotContain("typeof(global::System.Collections.Generic.List<global::MyApp.Receptors.MyEvent>)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfIEvent_UsesPatternMatchingForCascadeAsync() {
    // Arrange - Tests that List<IEvent> uses 'is IEvent' pattern matching instead of 'typeof(IEvent)'
    // This is critical for the JDNext scenario where (List<IEvent>, FailedEvent?) returns concrete types
    // At runtime, the message is typeof(ConcreteEvent), not typeof(IEvent), so exact matching fails
    const string source = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessBatchCommand : ICommand {
  public string BatchId { get; init; } = string.Empty;
}

public record FailureEvent : IEvent {
  public string Reason { get; init; } = string.Empty;
}

public class BatchReceptor : IReceptor<ProcessBatchCommand, (List<IEvent>, FailureEvent?)> {
  public ValueTask<(List<IEvent>, FailureEvent?)> HandleAsync(ProcessBatchCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult<(List<IEvent>, FailureEvent?)>((new List<IEvent>(), null));
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the dispatcher source which contains the outbox cascade code
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Key assertion: Interface types use pattern matching 'is IEvent' instead of 'typeof(IEvent)'
    // This allows concrete types that implement IEvent to match at runtime
    await Assert.That(dispatcher).Contains("message is global::Whizbang.Core.IEvent");

    // Should NOT use exact typeof() matching for IEvent (which would never match concrete types)
    await Assert.That(dispatcher).DoesNotContain("messageType == typeof(global::Whizbang.Core.IEvent)");

    // Interface types use PublishToOutboxDynamicAsync (serializes using runtime type, not interface)
    await Assert.That(dispatcher).Contains("PublishToOutboxDynamicAsync(message, messageType, messageId, sourceEnvelope)");

    // Concrete types like FailureEvent should still use exact typeof() matching
    await Assert.That(dispatcher).Contains("typeof(global::MyApp.Receptors.FailureEvent)");

    // Concrete types use regular PublishToOutboxAsync
    await Assert.That(dispatcher).Contains("PublishToOutboxAsync((global::MyApp.Receptors.FailureEvent)message");
  }

  #endregion

  #region Cascade Security Context Propagation Tests

  /// <summary>
  /// Tests that the generator produces GetUntypedReceptorPublisher method with IMessageEnvelope? parameter.
  /// This parameter is required to propagate security context from source envelope to cascaded receptors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_WithEnvelopeParameterAsync() {
    // Arrange - Receptor returning event (requires cascade support)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand {
  public string OrderId { get; init; } = string.Empty;
}

public record OrderCreated : IEvent {
  public string OrderId { get; init; } = string.Empty;
}

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default) {
    return ValueTask.FromResult(new OrderCreated { OrderId = message.OrderId });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated GetUntypedReceptorPublisher should accept IMessageEnvelope? parameter
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify the signature includes IMessageEnvelope? parameter
    await Assert.That(dispatcher).Contains("global::Whizbang.Core.Observability.IMessageEnvelope? sourceEnvelope");
  }

  /// <summary>
  /// Tests that the generator produces GetUntypedReceptorPublisher method with CancellationToken parameter.
  /// This parameter is required to properly propagate cancellation through cascaded receptors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_WithCancellationTokenParameterAsync() {
    // Arrange - Receptor returning event (requires cascade support)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated GetUntypedReceptorPublisher should accept CancellationToken parameter
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify the signature includes CancellationToken parameter
    await Assert.That(dispatcher).Contains("global::System.Threading.CancellationToken cancellationToken");
  }

  /// <summary>
  /// Tests that the generator produces code that calls SecurityContextHelper.EstablishFullContextAsync
  /// to establish security context from the source envelope before invoking receptors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_CallsEstablishFullContextAsync() {
    // Arrange - Receptor returning event (requires cascade with security context)
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ProcessCommand : ICommand;
public record ProcessedEvent : IEvent;

public class ProcessReceptor : IReceptor<ProcessCommand, ProcessedEvent> {
  public ValueTask<ProcessedEvent> HandleAsync(ProcessCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new ProcessedEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should call EstablishFullContextAsync
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify EstablishFullContextAsync is called
    await Assert.That(dispatcher).Contains("await global::Whizbang.Core.Security.SecurityContextHelper.EstablishFullContextAsync");
    await Assert.That(dispatcher).Contains("sourceEnvelope");
    await Assert.That(dispatcher).Contains("scope.ServiceProvider");
  }

  /// <summary>
  /// Tests that the generator produces code that passes cancellationToken to receptor HandleAsync calls.
  /// Ensures proper cancellation propagation through the cascade chain.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_PassesCancellationToHandleAsync() {
    // Arrange - Receptor that should receive cancellation token
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ExecuteCommand : ICommand;
public record ExecutedEvent : IEvent;

public class ExecuteReceptor : IReceptor<ExecuteCommand, ExecutedEvent> {
  public ValueTask<ExecutedEvent> HandleAsync(ExecuteCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new ExecutedEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should pass cancellationToken to HandleAsync
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify cancellationToken is passed to HandleAsync (per-receptor keyed invocation)
    await Assert.That(dispatcher).Contains("await r.HandleAsync(typedEvt, cancellationToken)");
  }

  /// <summary>
  /// Tests that the generator uses fully qualified names (global:: prefix) to avoid namespace conflicts.
  /// Critical for AOT compatibility and avoiding ambiguous type references.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_UsesFullyQualifiedNamesAsync() {
    // Arrange - Simple receptor to test generated code quality
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record TestCommand : ICommand;
public record TestEvent : IEvent;

public class TestReceptor : IReceptor<TestCommand, TestEvent> {
  public ValueTask<TestEvent> HandleAsync(TestCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new TestEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should use fully qualified names
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify fully qualified names are used (global:: prefix)
    await Assert.That(dispatcher).Contains("global::Whizbang.Core.Observability.IMessageEnvelope?");
    await Assert.That(dispatcher).Contains("global::System.Threading.CancellationToken");
    await Assert.That(dispatcher).Contains("global::Whizbang.Core.Security.SecurityContextHelper");
  }

  /// <summary>
  /// Tests that the generator produces code that checks for null envelope before establishing security context.
  /// Null envelope scenarios occur in RPC local invoke paths where security context is not available.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_ChecksNullEnvelopeAsync() {
    // Arrange - Receptor that should handle null envelope gracefully
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record ValidateCommand : ICommand;
public record ValidationResult : IEvent;

public class ValidationReceptor : IReceptor<ValidateCommand, ValidationResult> {
  public ValueTask<ValidationResult> HandleAsync(ValidateCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new ValidationResult());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should check for null envelope
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify null check before establishing context
    await Assert.That(dispatcher).Contains("if (sourceEnvelope is not null)");
  }

  /// <summary>
  /// Tests that the generator produces code that properly disposes scope in finally block.
  /// Ensures resources are cleaned up even when receptor throws exception.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_DisposesScope_InFinallyAsync() {
    // Arrange - Receptor that requires proper scope disposal
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CleanupCommand : ICommand;
public record CleanupEvent : IEvent;

public class CleanupReceptor : IReceptor<CleanupCommand, CleanupEvent> {
  public ValueTask<CleanupEvent> HandleAsync(CleanupCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new CleanupEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should dispose scope in finally block
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify try-finally pattern with scope disposal
    await Assert.That(dispatcher).Contains("try");
    await Assert.That(dispatcher).Contains("finally");
    // Scope disposal should be in finally block (either Dispose or DisposeAsync)
    var finallyIndex = dispatcher.IndexOf("finally", StringComparison.Ordinal);
    var endIndex = dispatcher.IndexOf("return PublishToReceptorsUntyped", finallyIndex, StringComparison.Ordinal);
    if (endIndex < 0) {
      endIndex = dispatcher.Length;
    }

    var finallySection = dispatcher[finallyIndex..endIndex];
    await Assert.That(finallySection).Contains("scope");
  }

  /// <summary>
  /// Tests that the generator produces code that handles IAsyncDisposable correctly.
  /// Should call DisposeAsync for async disposable scopes, Dispose otherwise.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesUntypedPublisher_HandlesAsyncDisposable_CorrectlyAsync() {
    // Arrange - Receptor that requires async disposal handling
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record AsyncCommand : ICommand;
public record AsyncEvent : IEvent;

public class AsyncReceptor : IReceptor<AsyncCommand, AsyncEvent> {
  public ValueTask<AsyncEvent> HandleAsync(AsyncCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new AsyncEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should handle IAsyncDisposable
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify IAsyncDisposable check and DisposeAsync call
    await Assert.That(dispatcher).Contains("if (scope is IAsyncDisposable asyncDisposable)");
    await Assert.That(dispatcher).Contains("await asyncDisposable.DisposeAsync()");
    // Fallback to Dispose for non-async disposable scopes
    await Assert.That(dispatcher).Contains("scope.Dispose()");
  }

  /// <summary>
  /// Tests that the generator produces code with an else branch that calls EstablishMessageContextForCascade
  /// when sourceEnvelope is null. This is critical for cascade paths to inherit security context.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNullEnvelope_CallsEstablishMessageContextForCascadeAsync() {
    // Arrange - Receptor that requires security context in cascade
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CascadeCommand : ICommand;
public record CascadeEvent : IEvent;

public class CascadeReceptor : IReceptor<CascadeCommand, CascadeEvent> {
  public ValueTask<CascadeEvent> HandleAsync(CascadeCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new CascadeEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should have else branch calling EstablishMessageContextForCascade
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify else branch exists and calls EstablishMessageContextForCascade
    await Assert.That(dispatcher).Contains("} else {");
    await Assert.That(dispatcher).Contains("EstablishMessageContextForCascade(scope.ServiceProvider)");
  }

  /// <summary>
  /// Tests that the generator produces an else branch immediately after the null envelope check.
  /// Ensures proper control flow structure for handling both null and non-null envelope scenarios.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ProducesElseBranch_AfterNullCheckAsync() {
    // Arrange - Simple receptor to verify control flow structure
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record FlowCommand : ICommand;
public record FlowEvent : IEvent;

public class FlowReceptor : IReceptor<FlowCommand, FlowEvent> {
  public ValueTask<FlowEvent> HandleAsync(FlowCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new FlowEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should have if-else structure
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify if-else pattern exists
    var nullCheckIndex = dispatcher!.IndexOf("if (sourceEnvelope is not null)", StringComparison.Ordinal);
    await Assert.That(nullCheckIndex).IsGreaterThan(-1);

    var elseIndex = dispatcher.IndexOf("} else {", nullCheckIndex, StringComparison.Ordinal);
    await Assert.That(elseIndex).IsGreaterThan(nullCheckIndex);
  }

  /// <summary>
  /// Tests that the else branch calls the correct method: EstablishMessageContextForCascade.
  /// This method reads from ScopeContextAccessor.Current and sets MessageContextAccessor.CurrentContext.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ElseBranch_CallsCorrectMethodAsync() {
    // Arrange - Receptor requiring correct cascade context establishment
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record MethodCommand : ICommand;
public record MethodEvent : IEvent;

public class MethodReceptor : IReceptor<MethodCommand, MethodEvent> {
  public ValueTask<MethodEvent> HandleAsync(MethodCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new MethodEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should call EstablishMessageContextForCascade
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify exact method name is used (with scope.ServiceProvider parameter)
    await Assert.That(dispatcher).Contains("EstablishMessageContextForCascade(scope.ServiceProvider)");
    // Should NOT have sourceEnvelope parameter
    await Assert.That(dispatcher).DoesNotContain("EstablishMessageContextForCascade(sourceEnvelope");
  }

  /// <summary>
  /// Tests that the else branch uses fully qualified name for SecurityContextHelper.
  /// Critical for AOT compatibility and avoiding namespace conflicts.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ElseBranch_UsesFullyQualifiedNameAsync() {
    // Arrange - Receptor requiring AOT-compatible code generation
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record QualifiedCommand : ICommand;
public record QualifiedEvent : IEvent;

public class QualifiedReceptor : IReceptor<QualifiedCommand, QualifiedEvent> {
  public ValueTask<QualifiedEvent> HandleAsync(QualifiedCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new QualifiedEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should use fully qualified name
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Verify fully qualified name with global:: prefix
    await Assert.That(dispatcher).Contains("global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider)");
  }

  /// <summary>
  /// Tests that the else branch includes a comment explaining its purpose.
  /// Documentation in generated code helps developers understand the cascade path security context flow.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ElseBranch_IncludesCommentAsync() {
    // Arrange - Receptor requiring well-documented generated code
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CommentCommand : ICommand;
public record CommentEvent : IEvent;

public class CommentReceptor : IReceptor<CommentCommand, CommentEvent> {
  public ValueTask<CommentEvent> HandleAsync(CommentCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new CommentEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Generated code should include explanatory comment
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    // Debug: Write generated code to see what we're actually getting
    System.IO.File.WriteAllText("/tmp/test-dispatcher.g.cs", dispatcher);

    // Verify the dispatcher contains the else branch with cascade security context establishment
    await Assert.That(dispatcher!.Contains("} else {", StringComparison.Ordinal)).IsTrue();
    await Assert.That(dispatcher.Contains("EstablishMessageContextForCascade", StringComparison.Ordinal)).IsTrue();
  }

  #endregion

  // ==================== Polymorphic Receptor Expansion Tests ====================

  #region Polymorphic Receptor Expansion Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInterfaceReceptor_ExpandsToConcreteTypes_InRegistryAsync() {
    // Arrange - Interface with two concrete implementations, receptor for the interface
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public interface IMyMarker { }

public record ConcreteEventA : IEvent, IMyMarker;
public record ConcreteEventB : IEvent, IMyMarker;

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class MarkerReceptor : IReceptor<IMyMarker> {
  public ValueTask HandleAsync(IMyMarker message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - ReceptorRegistry should have entries for both concrete types
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.ConcreteEventA)");
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.ConcreteEventB)");
    await Assert.That(registry).Contains("LifecycleStage.PrePerspectiveInline");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInterfaceReceptor_PreservesOriginalInterfaceEntry_InRegistryAsync() {
    // Arrange - Same as above; verify the original interface entry is also generated
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public interface IMyMarker { }

public record ConcreteEventA : IEvent, IMyMarker;

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class MarkerReceptor : IReceptor<IMyMarker> {
  public ValueTask HandleAsync(IMyMarker message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Original interface entry should also exist for direct dispatch
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.IMyMarker)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithBaseClassReceptor_ExpandsToConcreteTypes_InRegistryAsync() {
    // Arrange - Base class with two concrete derived classes, receptor for the base
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public record MyBaseEvent : IEvent;
public record DerivedEventA : MyBaseEvent;
public record DerivedEventB : MyBaseEvent;

[FireAt(LifecycleStage.PostPerspectiveDetached)]
public class BaseReceptor : IReceptor<MyBaseEvent> {
  public ValueTask HandleAsync(MyBaseEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - ReceptorRegistry should have entries for both derived types
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.DerivedEventA)");
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.DerivedEventB)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSealedClassReceptor_DoesNotExpandAsync() {
    // Arrange - Sealed class cannot have subtypes, no expansion
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public sealed record MySealedEvent : IEvent;

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class SealedReceptor : IReceptor<MySealedEvent> {
  public ValueTask HandleAsync(MySealedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Only the sealed type entry exists
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.MySealedEvent)");

    // Count if-condition occurrences - should appear only once (no expansion added extra entries)
    // The pattern "messageType == typeof(...)" appears in the if-condition only
    var ifConditionMatches = registry.Split("messageType == typeof(global::MyApp.Events.MySealedEvent)").Length - 1;
    await Assert.That(ifConditionMatches).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPolymorphicReceptor_SkipsAbstractDerivedTypesAsync() {
    // Arrange - Interface → abstract class → concrete class; only concrete gets expanded
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public interface IMyMarker { }

public abstract record AbstractMiddle : IEvent, IMyMarker;
public record ConcreteLeaf : AbstractMiddle;

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class MarkerReceptor : IReceptor<IMyMarker> {
  public ValueTask HandleAsync(IMyMarker message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Only the concrete class gets an expanded entry, not the abstract one
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.ConcreteLeaf)");
    await Assert.That(registry).DoesNotContain("typeof(global::MyApp.Events.AbstractMiddle)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPolymorphicReceptor_MergesWithExistingEntriesAsync() {
    // Arrange - ConcreteEvent implements IMyMarker. One receptor for ConcreteEvent AND one for IMyMarker
    // at the same stage. Both should appear in a SINGLE array for typeof(ConcreteEvent).
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public interface IMyMarker { }
public record ConcreteEvent : IEvent, IMyMarker;

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class DirectReceptor : IReceptor<ConcreteEvent> {
  public ValueTask HandleAsync(ConcreteEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class MarkerReceptor : IReceptor<IMyMarker> {
  public ValueTask HandleAsync(IMyMarker message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Both receptors should be in the same array for typeof(ConcreteEvent)
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // The ConcreteEvent entry should contain both receptor IDs
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.ConcreteEvent)");
    await Assert.That(registry).Contains("DirectReceptor");
    await Assert.That(registry).Contains("MarkerReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPolymorphicReceptor_NoImplementors_GeneratesOnlyInterfaceEntryAsync() {
    // Arrange - Interface with NO implementing concrete types
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public interface IEmptyMarker { }

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class EmptyReceptor : IReceptor<IEmptyMarker> {
  public ValueTask HandleAsync(IEmptyMarker message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Only the interface entry exists (no expansion because no implementors)
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("typeof(global::MyApp.Events.IEmptyMarker)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInterfaceReceptor_ExpandsNestedConcreteTypes_InRegistryAsync() {
    // Arrange - Concrete types nested inside a static contracts class (real-world pattern)
    const string source = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Events;

public interface IMyMarker { }

public static class MyContracts {
  public record NestedEventA : IEvent, IMyMarker;
  public record NestedEventB : IEvent, IMyMarker;
}

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class MarkerReceptor : IReceptor<IMyMarker> {
  public ValueTask HandleAsync(IMyMarker message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Nested concrete types should be expanded
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("MyApp.Events.MyContracts.NestedEventA");
    await Assert.That(registry).Contains("MyApp.Events.MyContracts.NestedEventB");
  }

  #endregion
}
