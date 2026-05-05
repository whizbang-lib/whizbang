using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ReceptorRegistryQueryGenerator — emits a static class that the receive boundary
/// uses to decide whether a message has any consumer (handler / perspective / lifecycle
/// receptor / tagged-notification attribute) without runtime reflection. See
/// plans/pump-then-process.md slice 1.
///
/// Locked invariants:
/// - HasReceptors(stage, type) → true iff a receptor with [FireAt(stage)] for that type is registered
/// - HasInboxHandler(type)     → true iff any IReceptor&lt;T,...&gt; or IReceptor&lt;T&gt; is registered
/// - HasAnyConsumer(type)      → true iff handler / perspective / lifecycle receptor / tag-attribute exists
/// </summary>
[Category("SourceGenerators")]
[Category("ReceptorRegistryQuery")]
public class ReceptorRegistryQueryGeneratorTests {

  // ===== HasInboxHandler =====

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInboxHandler_HasInboxHandlerReturnsTrueAsync() {
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp;

public record CreateOrder : ICommand { public string OrderId { get; init; } = string.Empty; }
public record OrderCreated : IEvent { public string OrderId { get; init; } = string.Empty; }

public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.FromResult(new OrderCreated { OrderId = message.OrderId });
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    // Generator records the type in its inbox-handler set
    await Assert.That(generated!).Contains("MyApp.CreateOrder");
    // Method is generated
    await Assert.That(generated).Contains("public static bool HasInboxHandler");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoInboxHandlerForType_HasInboxHandlerReturnsFalseAsync() {
    // Type exists but no IReceptor for it
    const string source = @"
using Whizbang.Core;

namespace MyApp;

public record OrphanCommand : ICommand { public string Id { get; init; } = string.Empty; }
";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    // The orphan type must NOT be in the inbox-handler set. We assert by checking that the
    // inbox-handler set initializer does not contain "MyApp.OrphanCommand".
    var handlerSetRegion = _extractRegion(generated!, "_typesWithInboxHandler");
    await Assert.That(handlerSetRegion).DoesNotContain("OrphanCommand");
  }

  // ===== HasReceptors (lifecycle stages) =====

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithFireAtPreInboxInline_HasReceptorsReturnsTrueForPreInboxInlineAsync() {
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp;

public record CreateOrder : ICommand { public string OrderId { get; init; } = string.Empty; }

[FireAt(LifecycleStage.PreInboxInline)]
public class PreInboxAuditReceptor : IReceptor<CreateOrder> {
  public ValueTask HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("public static bool HasReceptors");
    // The compiled lookup must include CreateOrder under PreInboxInline.
    // We assert the generated code mentions both the type and the stage.
    await Assert.That(generated).Contains("MyApp.CreateOrder");
    await Assert.That(generated).Contains("PreInboxInline");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoLifecycleReceptors_HasReceptorsReturnsFalseAsync() {
    // Direct handler, no [FireAt] → should NOT be marked as PreInbox/PostInbox lifecycle
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace MyApp;

public record CreateOrder : ICommand { public string OrderId { get; init; } = string.Empty; }

public class OrderReceptor : IReceptor<CreateOrder> {
  public ValueTask HandleAsync(CreateOrder message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    // The PreInboxInline lookup region should not contain CreateOrder for this case
    // (the receptor is a direct handler, not a PreInbox lifecycle receptor).
    var preInboxRegion = _extractStageRegion(generated!, "PreInboxInline");
    await Assert.That(preInboxRegion).DoesNotContain("CreateOrder");
  }

  // ===== HasAnyConsumer — covers handler / perspective / tag-attribute =====

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspectiveOnly_HasAnyConsumerReturnsTrueAsync() {
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp;

public record OrderCreated : IEvent { public string OrderId { get; init; } = string.Empty; }
public record OrderModel { public string Id { get; set; } = string.Empty; }

public class OrderProjection : IPerspectiveFor<OrderModel, OrderCreated> {
  public ApplyResult<OrderModel> Apply(OrderModel current, OrderCreated @event)
    => ApplyResult<OrderModel>.Update(new OrderModel { Id = @event.OrderId });
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("public static bool HasAnyConsumer");
    // OrderCreated is consumed by a perspective even with no receptor
    await Assert.That(generated).Contains("MyApp.OrderCreated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_OrphanType_HasAnyConsumerReturnsFalseAsync() {
    const string source = @"
using Whizbang.Core;

namespace MyApp;

public record OrphanEvent : IEvent { public string Id { get; init; } = string.Empty; }
";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    // No handler, no perspective, no tag attribute → must not be in any consumer set.
    var consumerRegion = _extractRegion(generated!, "_typesWithAnyConsumer");
    await Assert.That(consumerRegion).DoesNotContain("OrphanEvent");
  }

  // ===== Generated file structure =====

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AlwaysEmitsRegistryQueryClassAsync() {
    // Even with no inputs (no receptors, no perspectives, no tag attributes), the registry
    // query class is always present so consumer code can call into it without conditional refs.
    const string source = @"
namespace MyApp;
public class Empty {}
";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQuery.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("public static class WhizbangReceptorRegistryQuery");
    await Assert.That(generated).Contains("HasReceptors");
    await Assert.That(generated).Contains("HasInboxHandler");
    await Assert.That(generated).Contains("HasAnyConsumer");
  }

  // ===== Helpers =====

  /// <summary>
  /// Extracts the body of a named static collection initializer for content assertions.
  /// Looks for "_typesWithInboxHandler" / "_typesWithAnyConsumer" / etc.
  /// </summary>
  private static string _extractRegion(string source, string fieldName) {
    var fieldStart = source.IndexOf(fieldName, StringComparison.Ordinal);
    if (fieldStart < 0) {
      return string.Empty;
    }
    var braceStart = source.IndexOf('{', fieldStart);
    if (braceStart < 0) {
      return string.Empty;
    }
    var depth = 0;
    for (var i = braceStart; i < source.Length; i++) {
      if (source[i] == '{') {
        depth++;
      } else if (source[i] == '}') {
        depth--;
        if (depth == 0) {
          return source[braceStart..(i + 1)];
        }
      }
    }
    return source[braceStart..];
  }

  /// <summary>
  /// Extracts the lookup region for a specific lifecycle stage. The generator emits one
  /// per-stage HashSet&lt;string&gt; named e.g. "_typesWith_PreInboxInline".
  /// </summary>
  private static string _extractStageRegion(string source, string stageName) {
    return _extractRegion(source, $"_typesWith_{stageName}");
  }
}
