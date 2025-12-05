using System.Diagnostics.CodeAnalysis;
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
  public async Task Generator_WithNoReceptors_GeneratesWarningAsync() {
    // Arrange - Tests WHIZ002 diagnostic when no receptors or perspectives found
    var source = @"
namespace MyApp;

public class SomeClass {
  public string Name { get; set; } = string.Empty;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should report WHIZ002 warning
    var warnings = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToArray();
    await Assert.That(warnings).HasCount().GreaterThanOrEqualTo(1);

    var whiz002 = warnings.FirstOrDefault(d => d.Id == "WHIZ002");
    await Assert.That(whiz002).IsNotNull();
    await Assert.That(whiz002!.GetMessage()).Contains("No IReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspectiveButNoReceptor_DoesNotWarnAsync() {
    // Arrange - Tests that IPerspectiveOf suppresses WHIZ002 warning
    var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Perspectives;

public record TestEvent : IEvent;

public class TestPerspective : IPerspectiveOf<TestEvent> {
  public Guid Id { get; set; }
  public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
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

    // Assert - Should skip classes without base list (caught by predicate)
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
    await Assert.That(whiz001!.GetMessage()).Contains("TestReceptor");
    await Assert.That(whiz001.GetMessage()).Contains("TestCommand");
    await Assert.That(whiz001.GetMessage()).Contains("TestResponse");
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
    await Assert.That(whiz001!.GetMessage()).Contains("void");
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

    // Assert - Should report WHIZ002 (no receptors found)
    var warnings = result.Diagnostics.Where(d => d.Id == "WHIZ002").ToArray();
    await Assert.That(warnings).HasCount().GreaterThanOrEqualTo(1);
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
    await Assert.That(whiz001!.GetMessage()).Contains("(OrderSummary, CustomerInfo)");
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
    await Assert.That(whiz001!.GetMessage()).Contains("OrderCreated[]");
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
    await Assert.That(whiz001!.GetMessage()).Contains("(OrderSummary, (CustomerInfo, ShippingInfo))");
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

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public class ProductCatalogPerspective : IPerspectiveOf<ProductCreatedEvent> {
  public Guid Id { get; set; }
  public Task Update(ProductCreatedEvent @event, CancellationToken ct = default) => Task.CompletedTask;
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

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public class ProductCatalogPerspective : IPerspectiveOf<ProductCreatedEvent> {
  public Guid Id { get; set; }
  public Task Update(ProductCreatedEvent @event, CancellationToken ct = default) => Task.CompletedTask;
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

namespace MyApp.Perspectives;

public record ProductCreatedEvent : IEvent;

public class ProductCatalogPerspective : IPerspectiveOf<ProductCreatedEvent> {
  public Guid Id { get; set; }
  public Task Update(ProductCreatedEvent @event, CancellationToken ct = default) => Task.CompletedTask;
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
}
