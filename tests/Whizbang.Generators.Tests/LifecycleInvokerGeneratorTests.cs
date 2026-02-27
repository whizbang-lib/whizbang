using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for LifecycleInvoker code generation in ReceptorDiscoveryGenerator.
/// Ensures [FireAt] lifecycle receptors can resolve scoped dependencies.
/// </summary>
/// <tests>Whizbang.Generators/ReceptorDiscoveryGenerator.cs:_generateLifecycleInvokerSource</tests>
[Category("SourceGenerators")]
[Category("LifecycleInvoker")]
public class LifecycleInvokerGeneratorTests {

  // ========================================
  // SCOPED DEPENDENCY RESOLUTION TESTS
  // These tests verify the fix for:
  // "Cannot resolve 'IReceptor<T>' from root provider because it requires scoped service"
  // ========================================

  /// <summary>
  /// Verifies that generated LifecycleInvoker uses IServiceScopeFactory instead of IServiceProvider.
  /// This is critical for resolving scoped dependencies like DbContext, IOrchestratorAgent, etc.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_LifecycleInvoker_UsesServiceScopeFactory_NotServiceProviderAsync() {
    // Arrange - Receptor with [FireAt] attribute (lifecycle receptor)
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record StartedEvent : IEvent;

[FireAt(LifecycleStage.PostDistributeInline)]
public class StartupLogger : IReceptor<StartedEvent> {
  public ValueTask HandleAsync(StartedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate LifecycleInvoker.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // CRITICAL: Generated code should use IServiceScopeFactory, not IServiceProvider directly
    await Assert.That(lifecycleInvoker!).Contains("IServiceScopeFactory");
    await Assert.That(lifecycleInvoker).Contains("_scopeFactory");

    // Should NOT have direct IServiceProvider field for service resolution
    // (it's okay to have IServiceProvider for registry lookup, but not for receptor resolution)
    await Assert.That(lifecycleInvoker).DoesNotContain("private readonly IServiceProvider _serviceProvider;");
  }

  /// <summary>
  /// Verifies that generated LifecycleInvoker creates a scope before resolving receptors.
  /// This ensures scoped dependencies are properly resolved within the scope lifecycle.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_LifecycleReceptorWithScopedDependency_CreatesScopeAsync() {
    // Arrange - Receptor with [FireAt] that depends on scoped services
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record StartedEvent : IEvent;

[FireAt(LifecycleStage.PostDistributeInline)]
public class StartupHandler : IReceptor<StartedEvent> {
  // This receptor might depend on scoped services like DbContext, IOrchestratorAgent, etc.
  public ValueTask HandleAsync(StartedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate LifecycleInvoker.g.cs with scope creation
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // CRITICAL: Generated code should create scope before resolving receptor
    await Assert.That(lifecycleInvoker!).Contains("CreateScope()");
    await Assert.That(lifecycleInvoker).Contains("scope.ServiceProvider.GetRequiredService");
  }

  /// <summary>
  /// Verifies that generated LifecycleInvoker disposes the scope after receptor invocation.
  /// This ensures proper resource cleanup and prevents memory leaks.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_LifecycleReceptorWithScopedDependency_DisposesScopeAsync() {
    // Arrange - Receptor with [FireAt]
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record ProcessedEvent : IEvent;

[FireAt(LifecycleStage.PostInboxInline)]
public class EventProcessor : IReceptor<ProcessedEvent> {
  public ValueTask HandleAsync(ProcessedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate LifecycleInvoker.g.cs with scope disposal
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // CRITICAL: Generated code should dispose the scope (using statement or try/finally)
    // Check for either using pattern or explicit disposal
    var hasUsingScope = lifecycleInvoker!.Contains("using var scope") || lifecycleInvoker.Contains("using (var scope");
    var hasAsyncDisposal = lifecycleInvoker.Contains("DisposeAsync()");
    var hasSyncDisposal = lifecycleInvoker.Contains("scope.Dispose()");
    var hasTryFinally = lifecycleInvoker.Contains("try") && lifecycleInvoker.Contains("finally");

    await Assert.That(hasUsingScope || hasAsyncDisposal || hasSyncDisposal || hasTryFinally)
        .IsTrue()
        .Because("Generated code should dispose the scope after receptor invocation");
  }

  /// <summary>
  /// Verifies that generated LifecycleInvoker constructor receives IServiceScopeFactory.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_LifecycleInvoker_ConstructorReceivesScopeFactoryAsync() {
    // Arrange - Receptor with [FireAt]
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record TestEvent : IEvent;

[FireAt(LifecycleStage.PreOutboxInline)]
public class TestHandler : IReceptor<TestEvent> {
  public ValueTask HandleAsync(TestEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate constructor with IServiceScopeFactory parameter
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // Constructor should take IServiceScopeFactory
    await Assert.That(lifecycleInvoker!).Contains("GeneratedLifecycleInvoker(IServiceScopeFactory");
  }

  // ========================================
  // RESPONSE TYPE LIFECYCLE RECEPTOR TESTS
  // ========================================

  /// <summary>
  /// Verifies that response-type lifecycle receptors also use scoped resolution.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_LifecycleReceptorWithResponse_UsesScopedResolutionAsync() {
    // Arrange - Response-type receptor with [FireAt]
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record ProcessCommand : ICommand;
public record ProcessedEvent : IEvent;

[FireAt(LifecycleStage.PostDistributeInline)]
public class ProcessHandler : IReceptor<ProcessCommand, ProcessedEvent> {
  public ValueTask<ProcessedEvent> HandleAsync(ProcessCommand message, CancellationToken ct = default)
    => ValueTask.FromResult(new ProcessedEvent());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should use scoped resolution for response-type receptor
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // Should use scope for response-type receptor too
    await Assert.That(lifecycleInvoker!).Contains("CreateScope()");
    await Assert.That(lifecycleInvoker).Contains("ProcessCommand");
  }

  // ========================================
  // MULTIPLE LIFECYCLE STAGE TESTS
  // ========================================

  /// <summary>
  /// Verifies that receptors with multiple [FireAt] attributes all use scoped resolution.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorWithMultipleFireAt_AllUsesScopedResolutionAsync() {
    // Arrange - Receptor with multiple lifecycle stages
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record AuditEvent : IEvent;

[FireAt(LifecycleStage.PreOutboxInline)]
[FireAt(LifecycleStage.PostInboxInline)]
public class AuditLogger : IReceptor<AuditEvent> {
  public ValueTask HandleAsync(AuditEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate routing for both stages
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // Both stages should be present
    await Assert.That(lifecycleInvoker!).Contains("PreOutboxInline");
    await Assert.That(lifecycleInvoker).Contains("PostInboxInline");

    // All should use scoped resolution
    await Assert.That(lifecycleInvoker).Contains("IServiceScopeFactory");
    await Assert.That(lifecycleInvoker).Contains("CreateScope()");
  }

  // ========================================
  // BASIC LIFECYCLE INVOKER GENERATION TESTS
  // ========================================

  /// <summary>
  /// Verifies that LifecycleInvoker.g.cs is generated when lifecycle receptors exist.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithLifecycleReceptor_GeneratesLifecycleInvokerAsync() {
    // Arrange
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record StartedEvent : IEvent;

[FireAt(LifecycleStage.PostDistributeInline)]
public class StartupLogger : IReceptor<StartedEvent> {
  public ValueTask HandleAsync(StartedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - Should generate LifecycleInvoker.g.cs
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();
    await Assert.That(lifecycleInvoker!).Contains("class GeneratedLifecycleInvoker");
    await Assert.That(lifecycleInvoker).Contains("ILifecycleInvoker");
  }

  /// <summary>
  /// Verifies that LifecycleInvoker includes routing for the correct lifecycle stage.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithLifecycleReceptor_IncludesCorrectStageRoutingAsync() {
    // Arrange
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

    // Assert - Should include routing for PostInboxInline stage
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();
    await Assert.That(lifecycleInvoker!).Contains("AuditEvent");
    await Assert.That(lifecycleInvoker).Contains("PostInboxInline");
  }

  /// <summary>
  /// Verifies that receptors without [FireAt] are NOT included in LifecycleInvoker routing.
  /// They should only be in the regular dispatcher routing at default stages.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutFireAt_NotIncludedInLifecycleInvokerRoutingAsync() {
    // Arrange - Receptor WITHOUT [FireAt] attribute
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp.Receptors;

public record CreateOrder : ICommand;
public record OrderCreated : IEvent;

// No [FireAt] - this is a regular business receptor
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated());
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - LifecycleInvoker should NOT contain routing for CreateOrder
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // The LIFECYCLE_ROUTING region should be empty or not contain CreateOrder
    // Look for the specific routing section - CreateOrder should NOT be there
    var routingSection = _extractRegionContent(lifecycleInvoker!, "LIFECYCLE_ROUTING");
    await Assert.That(routingSection).DoesNotContain("CreateOrder");
  }

  /// <summary>
  /// Helper to extract content between region markers (simplified).
  /// </summary>
  private static string _extractRegionContent(string source, string regionName) {
    var startMarker = $"// Generated compile-time routing";
    var startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
    if (startIndex < 0) {
      return string.Empty;
    }

    // Look for the next registry check
    var endMarker = "// Check for runtime-registered";
    var endIndex = source.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
    if (endIndex < 0) {
      endIndex = source.Length;
    }

    return source.Substring(startIndex, endIndex - startIndex);
  }

  // ========================================
  // STAGE ISOLATION TESTS
  // These tests verify that generated code includes explicit stage checks,
  // ensuring receptors ONLY fire at their registered stage.
  // Critical for PostPerspectiveAsync to fire AFTER perspective processing.
  // ========================================

  /// <summary>
  /// CRITICAL TEST: Verifies that generated code contains explicit stage check for PostPerspectiveAsync.
  /// If missing, the receptor would fire at ANY stage with matching message type.
  /// </summary>
  /// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
  [Test]
  [Category("StageIsolation")]
  [Category("PostPerspectiveAsync")]
  [RequiresAssemblyFiles()]
  public async Task Generator_PostPerspectiveAsyncReceptor_GeneratesExplicitStageCheckAsync() {
    // Arrange - Receptor with [FireAt(LifecycleStage.PostPerspectiveAsync)]
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record ModelUpdatedEvent : IEvent;

[FireAt(LifecycleStage.PostPerspectiveAsync)]
public class PostPerspectiveHandler : IReceptor<ModelUpdatedEvent> {
  public ValueTask HandleAsync(ModelUpdatedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // CRITICAL: Generated code MUST contain explicit stage check for PostPerspectiveAsync
    // This ensures the receptor ONLY fires at PostPerspectiveAsync, NOT at PrePerspectiveAsync
    await Assert.That(lifecycleInvoker!)
        .Contains("stage ==")
        .Because("Generated routing MUST check stage to ensure receptor fires only at registered stage");

    await Assert.That(lifecycleInvoker)
        .Contains("PostPerspectiveAsync")
        .Because("Generated routing MUST reference PostPerspectiveAsync stage");

    // Verify the stage check pattern: both message type AND stage must be checked
    var routingSection = _extractRegionContent(lifecycleInvoker, "LIFECYCLE_ROUTING");
    await Assert.That(routingSection).Contains("ModelUpdatedEvent")
        .Because("Generated routing should contain the message type");
    await Assert.That(routingSection).Contains("stage ==")
        .Because("Generated routing MUST have stage == check for stage isolation");
  }

  /// <summary>
  /// Verifies that PostPerspectiveAsync receptor generates combined condition with message type AND stage.
  /// Pattern: if (messageType == typeof(X) && stage == LifecycleStage.PostPerspectiveAsync)
  /// </summary>
  [Test]
  [Category("StageIsolation")]
  [Category("PostPerspectiveAsync")]
  [RequiresAssemblyFiles()]
  public async Task Generator_PostPerspectiveAsyncReceptor_GeneratesCombinedMessageTypeAndStageCheckAsync() {
    // Arrange
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record PerspectiveProcessedEvent : IEvent;

[FireAt(LifecycleStage.PostPerspectiveAsync)]
public class DataQueryHandler : IReceptor<PerspectiveProcessedEvent> {
  public ValueTask HandleAsync(PerspectiveProcessedEvent message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // CRITICAL: Must have BOTH messageType AND stage in the same condition
    // The pattern should be: if (messageType == typeof(X) && stage == Y)
    await Assert.That(lifecycleInvoker!)
        .Contains("&&")
        .Because("Generated routing should use && to combine message type and stage checks");

    // Both checks must appear together in the routing section
    var routingSection = _extractRegionContent(lifecycleInvoker, "LIFECYCLE_ROUTING");
    await Assert.That(routingSection).Contains("messageType == typeof")
        .Because("Generated routing should check message type");
    await Assert.That(routingSection).Contains("stage ==")
        .Because("Generated routing should check stage");
  }

  /// <summary>
  /// Verifies all 4 perspective lifecycle stages generate explicit stage checks.
  /// PrePerspectiveAsync, PrePerspectiveInline, PostPerspectiveAsync, PostPerspectiveInline
  /// </summary>
  [Test]
  [Category("StageIsolation")]
  [RequiresAssemblyFiles()]
  public async Task Generator_AllPerspectiveStages_GenerateExplicitStageChecksAsync() {
    // Arrange - One receptor for each perspective stage
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record TestEvent1 : IEvent;
public record TestEvent2 : IEvent;
public record TestEvent3 : IEvent;
public record TestEvent4 : IEvent;

[FireAt(LifecycleStage.PrePerspectiveAsync)]
public class PreAsyncHandler : IReceptor<TestEvent1> {
  public ValueTask HandleAsync(TestEvent1 message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

[FireAt(LifecycleStage.PrePerspectiveInline)]
public class PreInlineHandler : IReceptor<TestEvent2> {
  public ValueTask HandleAsync(TestEvent2 message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

[FireAt(LifecycleStage.PostPerspectiveAsync)]
public class PostAsyncHandler : IReceptor<TestEvent3> {
  public ValueTask HandleAsync(TestEvent3 message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

[FireAt(LifecycleStage.PostPerspectiveInline)]
public class PostInlineHandler : IReceptor<TestEvent4> {
  public ValueTask HandleAsync(TestEvent4 message, CancellationToken ct = default) => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    // All 4 perspective stages should be present with stage checks
    await Assert.That(lifecycleInvoker!).Contains("PrePerspectiveAsync")
        .Because("Generated routing should include PrePerspectiveAsync stage");
    await Assert.That(lifecycleInvoker).Contains("PrePerspectiveInline")
        .Because("Generated routing should include PrePerspectiveInline stage");
    await Assert.That(lifecycleInvoker).Contains("PostPerspectiveAsync")
        .Because("Generated routing should include PostPerspectiveAsync stage");
    await Assert.That(lifecycleInvoker).Contains("PostPerspectiveInline")
        .Because("Generated routing should include PostPerspectiveInline stage");

    // Each should have a stage check
    var routingSection = _extractRegionContent(lifecycleInvoker, "LIFECYCLE_ROUTING");

    // Count occurrences of "stage ==" - should be 4 (one per receptor)
    var stageCheckCount = System.Text.RegularExpressions.Regex.Count(routingSection, @"stage\s*==");
    await Assert.That(stageCheckCount).IsGreaterThanOrEqualTo(4)
        .Because("Each perspective stage receptor should have its own stage == check");
  }

  /// <summary>
  /// Verifies that different message types with different stages are properly isolated.
  /// </summary>
  [Test]
  [Category("StageIsolation")]
  [RequiresAssemblyFiles()]
  public async Task Generator_DifferentMessagesAtDifferentStages_ProperlyIsolatedAsync() {
    // Arrange - Same message type at different stages (via different receptors)
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record SharedEvent : IEvent;

[FireAt(LifecycleStage.PrePerspectiveAsync)]
public class PreHandler : IReceptor<SharedEvent> {
  public ValueTask HandleAsync(SharedEvent message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

[FireAt(LifecycleStage.PostPerspectiveAsync)]
public class PostHandler : IReceptor<SharedEvent> {
  public ValueTask HandleAsync(SharedEvent message, CancellationToken ct = default) => ValueTask.CompletedTask;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var lifecycleInvoker = GeneratorTestHelper.GetGeneratedSource(result, "LifecycleInvoker.g.cs");
    await Assert.That(lifecycleInvoker).IsNotNull();

    var routingSection = _extractRegionContent(lifecycleInvoker!, "LIFECYCLE_ROUTING");

    // Both handlers should have SharedEvent but with different stage checks
    // This ensures PreHandler fires at PrePerspectiveAsync and PostHandler fires at PostPerspectiveAsync
    await Assert.That(routingSection).Contains("PrePerspectiveAsync")
        .Because("PreHandler should register at PrePerspectiveAsync");
    await Assert.That(routingSection).Contains("PostPerspectiveAsync")
        .Because("PostHandler should register at PostPerspectiveAsync");

    // Count SharedEvent occurrences - should be 2 (one per handler)
    var sharedEventCount = System.Text.RegularExpressions.Regex.Count(routingSection, @"SharedEvent");
    await Assert.That(sharedEventCount).IsGreaterThanOrEqualTo(2)
        .Because("Both PreHandler and PostHandler should have routing for SharedEvent");
  }
}
