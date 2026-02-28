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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("namespace TestAssembly.Generated");
    await Assert.That(dispatcher).Contains("class GeneratedDispatcher");
    await Assert.That(dispatcher).Contains("CreateOrder");
    await Assert.That(dispatcher).Contains("OrderCreated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidReceptor_GeneratesDispatcherAsync() {
    // Arrange - Tests IReceptor<TMessage> (void receptor pattern)
    var source = @"
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
    await Assert.That(dispatcher!).Contains("LogMessage");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleReceptors_GeneratesAllRoutesAsync() {
    // Arrange
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CreateOrder");
    await Assert.That(dispatcher).Contains("UpdateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoReceptors_GeneratesInfoAsync() {
    // Arrange - Tests WHIZ002 diagnostic when no receptors or perspectives found
    var source = @"
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
    var source = @"
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
    var source = @"
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
    await Assert.That(registrations!).Contains("AddWhizbangDispatcher");
    await Assert.That(registrations).Contains("TestReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassNoBaseList_SkipsAsync() {
    // Arrange - Tests syntactic predicate filtering
    var source = @"
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
    var source = @"
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("TestCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleReceptorInterfaces_GeneratesForFirstAsync() {
    // Arrange - Tests class implementing multiple IReceptor interfaces
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CommandA");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTypeInGlobalNamespace_HandlesCorrectlyAsync() {
    // Arrange - Tests GetSimpleName with no dots (lastDot < 0)
    var source = @"
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
    await Assert.That(dispatcher!).Contains("GlobalCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesReceptorDiscoveryDiagnosticsAsync() {
    // Arrange - Tests ReceptorDiscoveryDiagnostics.g.cs generation
    var source = @"
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
    await Assert.That(diagnostics!).Contains("namespace TestAssembly.Generated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidReceptor_ReportsVoidResponseAsync() {
    // Arrange - Tests IsVoid branch in diagnostic reporting
    var source = @"
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
    var source = @"
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("GetOrderDetails");

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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("GetOrders");

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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("GetComplexOrder");

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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("class GeneratedDispatcher");
    await Assert.That(dispatcher).Contains("return null"); // All lookups return null
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_WithPerspective_GeneratesAddReceptorsAsync() {
    // Arrange - Should generate AddReceptors() extension even with 0 receptors
    var source = @"
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
    await Assert.That(registrations!).Contains("AddReceptors");
    await Assert.That(registrations).Contains("AddWhizbangDispatcher");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_WithPerspective_GeneratedCodeCompilesAsync() {
    // Arrange - Verify generated code compiles successfully
    var source = @"
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

  // ==================== Sync Receptor Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSyncReceptor_GeneratesDispatcherAsync() {
    // Arrange - Tests ISyncReceptor<TMessage, TResponse> discovery
    var source = @"
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
    await Assert.That(dispatcher!).Contains("namespace TestAssembly.Generated");
    await Assert.That(dispatcher).Contains("class GeneratedDispatcher");
    await Assert.That(dispatcher).Contains("CreateOrder");
    await Assert.That(dispatcher).Contains("GetSyncReceptorInvoker");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidSyncReceptor_GeneratesDispatcherAsync() {
    // Arrange - Tests ISyncReceptor<TMessage> (void sync receptor pattern)
    var source = @"
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
    await Assert.That(dispatcher!).Contains("LogMessage");
    await Assert.That(dispatcher).Contains("GetVoidSyncReceptorInvoker");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSyncReceptor_GeneratesRegistrationAsync() {
    // Arrange - Tests ISyncReceptor registration generation
    var source = @"
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
    await Assert.That(registrations!).Contains("ISyncReceptor<");
    await Assert.That(registrations).Contains("SyncOrderReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithBothSyncAndAsyncReceptors_GeneratesBothRoutesAsync() {
    // Arrange - Tests mixed sync and async receptor discovery
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CreateOrder"); // Async
    await Assert.That(dispatcher).Contains("UpdateOrder"); // Sync
    await Assert.That(dispatcher).Contains("GetReceptorInvoker"); // Async routing
    await Assert.That(dispatcher).Contains("GetSyncReceptorInvoker"); // Sync routing

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations!).Contains("IReceptor<"); // Async registration
    await Assert.That(registrations).Contains("ISyncReceptor<"); // Sync registration
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSyncReceptor_ReportsDiagnosticAsync() {
    // Arrange - Tests that sync receptors are reported in diagnostics
    var source = @"
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
    var source = @"
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
    await Assert.That(registry!).Contains("class GeneratedReceptorRegistry");
    await Assert.That(registry).Contains("IReceptorRegistry");
    await Assert.That(registry).Contains("GetReceptorsFor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorWithoutFireAt_RegisteredAtDefaultStagesAsync() {
    // Arrange - Receptor without [FireAt] attribute should be registered at 3 default stages
    var source = @"
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

    // Assert - Should register at LocalImmediateInline, PreOutboxInline, PostInboxInline
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("LifecycleStage.LocalImmediateInline");
    await Assert.That(registry).Contains("LifecycleStage.PreOutboxInline");
    await Assert.That(registry).Contains("LifecycleStage.PostInboxInline");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorWithFireAt_RegisteredOnlyAtSpecifiedStageAsync() {
    // Arrange - Receptor with [FireAt(PostInboxInline)] should only be registered at PostInboxInline
    var source = @"
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

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorRegistry_HasCorrectStructureAsync() {
    // Arrange
    var source = @"
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
    await Assert.That(registry!).Contains("sealed class GeneratedReceptorRegistry");
    // NOTE: GeneratedReceptorRegistry no longer has IServiceProvider field.
    // Instead, the InvokeAsync delegate accepts (sp, msg, ct) where sp is the scoped provider.
    await Assert.That(registry).Contains("GetReceptorsFor(Type messageType, LifecycleStage stage)");
    await Assert.That(registry).Contains("ReceptorInfo[]");
    await Assert.That(registry).Contains("ReceptorId:");
    await Assert.That(registry).Contains("InvokeAsync:");
    // Verify the delegate signature accepts IServiceProvider as first parameter (sp)
    await Assert.That(registry).Contains("(sp, msg, ct)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DispatcherRegistrations_IncludesAddWhizbangReceptorRegistryAsync() {
    // Arrange
    var source = @"
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
    await Assert.That(registrations!).Contains("AddWhizbangReceptorRegistry");
    await Assert.That(registrations).Contains("IReceptorRegistry, GeneratedReceptorRegistry");
    await Assert.That(registrations).Contains("IReceptorInvoker, ReceptorInvoker");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ZeroReceptors_GeneratesEmptyReceptorRegistryAsync() {
    // Arrange - Project with perspective but no receptors
    var source = @"
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
    await Assert.That(registry!).Contains("class GeneratedReceptorRegistry");
    await Assert.That(registry).Contains("return _emptyList");
  }

  #region DefaultRouting Attribute Detection Tests

  /// <summary>
  /// Tests that the generator detects [DefaultRouting] attribute on receptors
  /// and generates routing metadata lookup method.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDefaultRoutingAttribute_GeneratesRoutingLookupAsync() {
    // Arrange - Receptor with [DefaultRouting(DispatchMode.Local)]
    var source = @"
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

[DefaultRouting(DispatchMode.Local)]
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
    await Assert.That(dispatcher!).Contains("GetReceptorDefaultRouting");
    await Assert.That(dispatcher).Contains("CreateCache");
    await Assert.That(dispatcher).Contains("DispatchMode.Local");
  }

  /// <summary>
  /// Tests that the generator generates null return for receptors without [DefaultRouting].
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutDefaultRoutingAttribute_ReturnsNullAsync() {
    // Arrange - Receptor WITHOUT [DefaultRouting]
    var source = @"
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
    await Assert.That(dispatcher!).Contains("GetReceptorDefaultRouting");
    await Assert.That(dispatcher).Contains("return null");
  }

  /// <summary>
  /// Tests that the generator handles multiple receptors with different routing attributes.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMixedRoutingAttributes_GeneratesCorrectLookupsAsync() {
    // Arrange - Multiple receptors with different routing
    var source = @"
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

[DefaultRouting(DispatchMode.Local)]
public class LocalReceptor : IReceptor<CacheCommand, CacheEvent> {
  public ValueTask<CacheEvent> HandleAsync(CacheCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new CacheEvent());
  }
}

[DefaultRouting(DispatchMode.Outbox)]
public class OutboxReceptor : IReceptor<OutboxCommand, OutboxEvent> {
  public ValueTask<OutboxEvent> HandleAsync(OutboxCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new OutboxEvent());
  }
}

[DefaultRouting(DispatchMode.Both)]
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
    await Assert.That(dispatcher!).Contains("GetReceptorDefaultRouting");
    await Assert.That(dispatcher).Contains("CacheCommand");
    await Assert.That(dispatcher).Contains("DispatchMode.Local");
    await Assert.That(dispatcher).Contains("OutboxCommand");
    await Assert.That(dispatcher).Contains("DispatchMode.Outbox");
    await Assert.That(dispatcher).Contains("BothCommand");
    await Assert.That(dispatcher).Contains("DispatchMode.Both");
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CascadeToOutboxAsync");
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CascadeToOutboxAsync");
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CascadeToOutboxAsync");
  }

  /// <summary>
  /// Tests that tuple response types extract events for cascade generation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTupleResponse_ExtractsEventsForCascadeAsync() {
    // Arrange - Receptor returning tuple with events
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CascadeToOutboxAsync");
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("PublishToOutboxAsync");
    await Assert.That(dispatcher).Contains("MessageId.New()");
  }

  /// <summary>
  /// Tests that array response types are handled in cascade generation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithArrayResponse_IncludesEventTypeInCascadeAsync() {
    // Arrange - Receptor returning array of events
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CascadeToOutboxAsync");
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
    var source = @"
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
    await Assert.That(dispatcher!).Contains("CascadeToOutboxAsync");
    // Should contain ProcessedEvent (the inner type) in cascade
    await Assert.That(dispatcher).Contains("ProcessedEvent");

    // Extract just the CascadeToOutboxAsync method content to verify no Routed<> in cascade
    var cascadeStart = dispatcher.IndexOf("CascadeToOutboxAsync", StringComparison.Ordinal);
    var cascadeEnd = dispatcher.IndexOf("CascadeToEventStoreOnlyAsync", StringComparison.Ordinal);
    if (cascadeEnd < 0) {
      cascadeEnd = dispatcher.Length;
    }
    var cascadeSection = dispatcher.Substring(cascadeStart, cascadeEnd - cascadeStart);

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
    var source = @"
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
    var cascadeSection = dispatcher.Substring(cascadeStart, cascadeEnd - cascadeStart);

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
    var source = @"
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
    var source = @"
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
    return ValueTask.FromResult((Route.Outbox(new SuccessEvent()), new Routed<FailureEvent>(default!, DispatchMode.None)));
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
    var cascadeSection = dispatcher.Substring(cascadeStart, cascadeEnd - cascadeStart);

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
    var source = @"
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
    await Assert.That(registrations!).Contains("FailureEvent?");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNullableTupleElement_StripsNullabilityInTypeofAsync() {
    // Arrange - Tests that nullable tuple elements have the '?' stripped in typeof() contexts
    // This prevents CS8639: The typeof operator cannot be used on a nullable reference type
    var source = @"
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
    await Assert.That(dispatcher!).DoesNotContain("typeof(global::MyApp.Receptors.FailureEvent?)");

    // Verify the non-nullable typeof() IS present (for outbox cascade)
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.FailureEvent)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListInTuple_ExtractsElementTypeForCascadeAsync() {
    // Arrange - Tests that List<T> inside a tuple extracts the element type for cascade
    // This is the exact scenario from JDNext where (List<IEvent>, FailedEvent?) was not cascading
    var source = @"
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
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.BatchEvent)");
    await Assert.That(dispatcher!).DoesNotContain("typeof(global::System.Collections.Generic.List<global::MyApp.Receptors.BatchEvent>)");

    // Also verify FailureEvent is extracted (without the nullable annotation)
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.FailureEvent)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListResponseType_ExtractsElementTypeForCascadeAsync() {
    // Arrange - Tests that List<T> as a direct response type extracts the element type for cascade
    var source = @"
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
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.MyEvent)");
    await Assert.That(dispatcher!).DoesNotContain("typeof(global::System.Collections.Generic.List<global::MyApp.Receptors.MyEvent>)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfIEvent_UsesPatternMatchingForCascadeAsync() {
    // Arrange - Tests that List<IEvent> uses 'is IEvent' pattern matching instead of 'typeof(IEvent)'
    // This is critical for the JDNext scenario where (List<IEvent>, FailedEvent?) returns concrete types
    // At runtime, the message is typeof(ConcreteEvent), not typeof(IEvent), so exact matching fails
    var source = @"
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
    await Assert.That(dispatcher!).Contains("message is global::Whizbang.Core.IEvent");

    // Should NOT use exact typeof() matching for IEvent (which would never match concrete types)
    await Assert.That(dispatcher!).DoesNotContain("messageType == typeof(global::Whizbang.Core.IEvent)");

    // Interface types use PublishToOutboxDynamicAsync (serializes using runtime type, not interface)
    await Assert.That(dispatcher!).Contains("PublishToOutboxDynamicAsync(message, messageType, messageId, sourceEnvelope)");

    // Concrete types like FailureEvent should still use exact typeof() matching
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.FailureEvent)");

    // Concrete types use regular PublishToOutboxAsync
    await Assert.That(dispatcher!).Contains("PublishToOutboxAsync((global::MyApp.Receptors.FailureEvent)message");
  }

  #endregion

  #region Tracing and Metrics Code Generation Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTraceHandlerAttribute_GeneratesTracingCodeAsync() {
    // Arrange - Tests that [TraceHandler] attribute generates tracing code
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record TracedCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record TracedEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

[TraceHandler]
public class TracedReceptor : IReceptor<TracedCommand, TracedEvent> {
  public ValueTask<TracedEvent> HandleAsync(TracedCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new TracedEvent { Id = message.Id });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the receptor registry source which contains the traced invoker code
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // Key assertions: Tracing code should be generated for [TraceHandler] receptor
    // Should include timing capture with IDebuggerAwareClock
    await Assert.That(registry!).Contains("IDebuggerAwareClock");
    await Assert.That(registry!).Contains("GetCurrentTimestamp");

    // Should include tracer calls
    await Assert.That(registry!).Contains("ITracer");
    await Assert.That(registry!).Contains("BeginHandlerTrace");
    await Assert.That(registry!).Contains("EndHandlerTrace");

    // Should include HandlerStatus tracking
    await Assert.That(registry!).Contains("HandlerStatus");

    // Should have hasTraceAttribute set to true for this receptor
    await Assert.That(registry!).Contains("true"); // hasTraceAttribute = true
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMetricHandlerAttribute_GeneratesMetricRecordingCodeAsync() {
    // Arrange - Tests that [MetricHandler] attribute generates metric recording code
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record MetricCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record MetricEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

[MetricHandler]
public class MetricReceptor : IReceptor<MetricCommand, MetricEvent> {
  public ValueTask<MetricEvent> HandleAsync(MetricCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new MetricEvent { Id = message.Id });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the receptor registry source
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // Key assertions: Metric recording code should be generated
    await Assert.That(registry!).Contains("IHandlerMetrics");
    await Assert.That(registry!).Contains("RecordInvocation");

    // Should include timing for metrics
    await Assert.That(registry!).Contains("startTime");
    await Assert.That(registry!).Contains("endTime");
    await Assert.That(registry!).Contains("durationMs");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithBothAttributes_GeneratesBothTracingAndMetricsAsync() {
    // Arrange - Tests that both [TraceHandler] and [MetricHandler] generate full instrumentation
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record FullInstrumentedCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record FullInstrumentedEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

[TraceHandler]
[MetricHandler]
public class FullInstrumentedReceptor : IReceptor<FullInstrumentedCommand, FullInstrumentedEvent> {
  public ValueTask<FullInstrumentedEvent> HandleAsync(FullInstrumentedCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new FullInstrumentedEvent { Id = message.Id });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the receptor registry source
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // Both tracing and metrics should be present
    await Assert.That(registry!).Contains("ITracer");
    await Assert.That(registry!).Contains("IHandlerMetrics");
    await Assert.That(registry!).Contains("BeginHandlerTrace");
    await Assert.That(registry!).Contains("RecordInvocation");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutTracingAttributes_DoesNotGenerateTracingCodeAsync() {
    // Arrange - Tests that receptors without [TraceHandler] or [MetricHandler] don't get tracing overhead
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record PlainCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record PlainEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

public class PlainReceptor : IReceptor<PlainCommand, PlainEvent> {
  public ValueTask<PlainEvent> HandleAsync(PlainCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new PlainEvent { Id = message.Id });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the receptor registry source
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // The plain receptor should use the non-traced snippet (no ITracer, no IHandlerMetrics in its section)
    // It should have basic invocation without tracing overhead
    await Assert.That(registry!).Contains("PlainReceptor");
    await Assert.That(registry!).Contains("PlainCommand");

    // Verify it has the standard InvokeAsync pattern (simpler than traced version)
    // The non-traced snippet uses sp.GetRequiredService directly without tracing setup
    await Assert.That(registry!).Contains("GetRequiredService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTraceHandlerVerbosity_IncludesVerbosityInGeneratedCodeAsync() {
    // Arrange - Tests that [TraceHandler(TraceVerbosity.Debug)] includes verbosity
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record DebugCommand : ICommand {
  public string Id { get; init; } = string.Empty;
}

public record DebugEvent : IEvent {
  public string Id { get; init; } = string.Empty;
}

[TraceHandler(TraceVerbosity.Debug)]
public class DebugReceptor : IReceptor<DebugCommand, DebugEvent> {
  public ValueTask<DebugEvent> HandleAsync(DebugCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(new DebugEvent { Id = message.Id });
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the receptor registry source
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // Should have verbosity value in the generated code (Debug = 4)
    await Assert.That(registry!).Contains("BeginHandlerTrace");
    // The verbosity should be passed to BeginHandlerTrace
    await Assert.That(registry!).Contains("4"); // Debug verbosity value
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVoidReceptorAndTraceHandler_GeneratesTracingCodeAsync() {
    // Arrange - Tests that void receptors with [TraceHandler] generate traced code
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record LogMessage : ICommand {
  public string Message { get; init; } = string.Empty;
}

[TraceHandler]
public class TracedLogReceptor : IReceptor<LogMessage> {
  public ValueTask HandleAsync(LogMessage message, CancellationToken ct = default) {
    return ValueTask.CompletedTask;
  }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get the receptor registry source
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");
    await Assert.That(registry).IsNotNull();

    // Void receptor with tracing should still have tracing code
    await Assert.That(registry!).Contains("ITracer");
    await Assert.That(registry!).Contains("BeginHandlerTrace");
    await Assert.That(registry!).Contains("EndHandlerTrace");
    await Assert.That(registry!).Contains("TracedLogReceptor");
  }

  #endregion
}
