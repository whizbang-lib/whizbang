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
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // The registration must list the inbox handler type in the contribution it
    // registers with AssemblyRegistry<ReceptorRegistryContribution>.
    var inboxHandlersRegion = _extractRegion(generated!, "InboxHandlerTypes");
    await Assert.That(inboxHandlersRegion).Contains("MyApp.CreateOrder");
    // The registration class is the new shape (post-2026-05-06 redesign)
    await Assert.That(generated!).Contains("WhizbangReceptorRegistryQueryRegistration");
    await Assert.That(generated).Contains("AssemblyRegistry<ReceptorRegistryContribution>.Register");
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

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // The orphan type must NOT appear in any of the contribution's lists.
    await Assert.That(generated!).DoesNotContain("OrphanCommand");
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

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // Per-stage StageTypes dictionary populated for PreInboxInline with CreateOrder.
    await Assert.That(generated!).Contains("LifecycleStage.PreInboxInline");
    await Assert.That(generated).Contains("MyApp.CreateOrder");
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

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // The PreInboxInline stage's array should NOT contain CreateOrder for this case
    // (the receptor is a direct handler, not a PreInbox lifecycle receptor). We check by
    // extracting the slice of the source between LifecycleStage.PreInboxInline and the
    // following stage marker (or the end of the StageTypes block).
    var preInboxSlice = _extractStageArrayLiteral(generated!, "PreInboxInline");
    await Assert.That(preInboxSlice).DoesNotContain("CreateOrder");
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

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // OrderCreated is consumed by a perspective even with no receptor — the contribution's
    // AnyConsumerTypes array must include it.
    var anyConsumerRegion = _extractRegion(generated!, "AnyConsumerTypes");
    await Assert.That(anyConsumerRegion).Contains("MyApp.OrderCreated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorAtPostAllPerspectives_HasAnyConsumerReturnsTrueAsync() {
    // Regression lock: a receptor at a stage outside _exposedStages (e.g.
    // PostAllPerspectivesDetached — used by JdxNotificationTagHook) MUST still register the
    // message type as a consumer. Without this, the slice 3 drop-gate at the receive boundary
    // would silently drop messages whose only consumer is at PostAllPerspectives or
    // PostLifecycle — losing tag notifications for cross-service events. The bug existed
    // pre-fix because anyConsumerTypes was built only from inboxHandlers + the 4 exposed
    // stage HashSets; receptors at non-exposed stages slipped through.
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace MyApp;

public record DraftJobUpdated : IEvent { public string DraftJobId { get; init; } = string.Empty; }

[FireAt(LifecycleStage.PostAllPerspectivesDetached)]
public class TagNotificationHook : IReceptor<DraftJobUpdated> {
  public ValueTask HandleAsync(DraftJobUpdated message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    var consumerRegion = _extractRegion(generated!, "AnyConsumerTypes");
    await Assert.That(consumerRegion).Contains("MyApp.DraftJobUpdated")
      .Because("A receptor at any lifecycle stage — including stages outside _exposedStages — must register its message type into the contribution's AnyConsumerTypes. Otherwise the drop-gate silently loses messages.");
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

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // No handler, no perspective, no tag attribute → must not appear anywhere.
    await Assert.That(generated!).DoesNotContain("OrphanEvent");
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

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    await Assert.That(generated).IsNotNull();
    // The generator MUST always emit the registration class so that consuming assemblies
    // contribute (even an empty contribution) to the AssemblyRegistry. Without this, an
    // assembly with no receptors would skip its module-init step and miss any future
    // contribution registration symmetry.
    await Assert.That(generated!).Contains("WhizbangReceptorRegistryQueryRegistration");
    await Assert.That(generated).Contains("[ModuleInitializer]");
    await Assert.That(generated).Contains("AssemblyRegistry<ReceptorRegistryContribution>.Register");
  }

  // ===== Helpers =====

  /// <summary>
  /// Extracts the body of a named property initializer for content assertions.
  /// Looks for "AnyConsumerTypes" / "InboxHandlerTypes" / etc.
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
  /// Extracts the array literal between <c>stageTypes[LifecycleStage.&lt;name&gt;]</c> and
  /// the next <c>}</c>. The generator emits per-stage entries like:
  /// <code>stageTypes[LifecycleStage.PreInboxInline] = new string[] { "...", };</code>
  /// </summary>
  private static string _extractStageArrayLiteral(string source, string stageName) {
    var marker = $"LifecycleStage.{stageName}";
    var idx = source.IndexOf(marker, StringComparison.Ordinal);
    if (idx < 0) {
      return string.Empty;
    }
    var braceStart = source.IndexOf('{', idx);
    if (braceStart < 0) {
      return string.Empty;
    }
    var braceEnd = source.IndexOf('}', braceStart);
    if (braceEnd < 0) {
      return source[braceStart..];
    }
    return source[braceStart..(braceEnd + 1)];
  }
}
