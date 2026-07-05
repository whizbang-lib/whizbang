using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage tests for <see cref="ReceptorDiscoveryGenerator"/> targeting branches not
/// exercised by the main test suite: open-generic receptor skip, [FireAt] stage enum
/// resolution (multi-stage and unknown values), the full [AwaitPerspectiveSync] pipeline
/// (event types, timeouts, fire behaviors, non-event messages), replay/idempotency
/// attribute flags, traced void registry snippets, sync-receptor default routing,
/// unresolvable polymorphic metadata names, tuple-with-array cascade extraction, and the
/// non-test-assembly early exit when no handlers exist.
/// </summary>
/// <tests>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</tests>
[Category("SourceGenerators")]
[Category("ReceptorDiscovery")]
public class ReceptorDiscoveryGeneratorCoverageTests {
  private const string DISPATCHER_FILE = "Dispatcher.g.cs";
  private const string REGISTRY_FILE = "ReceptorRegistry.g.cs";
  private const string REGISTRATIONS_FILE = "DispatcherRegistrations.g.cs";

  // ==================== Open generic receptor skip ====================

  /// <summary>
  /// Open generic receptor classes (unbound type parameters) cannot be routed at
  /// compile time and must be skipped by the extraction transform, while sibling
  /// non-generic receptors are still discovered.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithOpenGenericReceptor_SkipsRegistrationAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp.Receptors;

      public sealed class PingCommand : ICommand { }

      public class GenericReceptor<T> : IReceptor<PingCommand> {
        public ValueTask HandleAsync(PingCommand message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }

      public class PingReceptor : IReceptor<PingCommand> {
        public ValueTask HandleAsync(PingCommand message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("PingReceptor");
    await Assert.That(registry!).DoesNotContain("GenericReceptor");

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, REGISTRATIONS_FILE);
    await Assert.That(registrations).IsNotNull();
    await Assert.That(registrations!).DoesNotContain("GenericReceptor");
  }

  // ==================== [FireAt] stage extraction ====================

  /// <summary>
  /// A receptor with multiple [FireAt] attributes must be registered at EVERY declared
  /// stage — and NOT at the default stages (LocalImmediateDetached/PostInboxDetached).
  /// Exercises the full _tryExtractFireAtStage enum-member resolution path.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleFireAtStages_RegistersReceptorAtEachStageAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace MyApp.Receptors;

      public sealed class ItemArchived : IEvent { }

      [FireAt(LifecycleStage.PostInboxInline)]
      [FireAt(LifecycleStage.PostPerspectiveInline)]
      public class AuditReceptor : IReceptor<ItemArchived> {
        public ValueTask HandleAsync(ItemArchived message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("global::Whizbang.Core.Messaging.LifecycleStage.PostInboxInline");
    await Assert.That(registry!).Contains("global::Whizbang.Core.Messaging.LifecycleStage.PostPerspectiveInline");
    await Assert.That(registry!).DoesNotContain("LifecycleStage.LocalImmediateDetached");
  }

  /// <summary>
  /// A [FireAt] with an int value that maps to NO LifecycleStage member must be silently
  /// skipped (no garbage emitted), leaving the receptor on the default stages.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithFireAtUnknownStageValue_FallsBackToDefaultStagesAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace MyApp.Receptors;

      public sealed class ItemArchived : IEvent { }

      [FireAt((LifecycleStage)999)]
      public class AuditReceptor : IReceptor<ItemArchived> {
        public ValueTask HandleAsync(ItemArchived message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    // Unknown stage value skipped -> receptor registered at the default stages
    await Assert.That(registry!).Contains("global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateDetached");
    await Assert.That(registry!).Contains("global::Whizbang.Core.Messaging.LifecycleStage.PostInboxDetached");
    await Assert.That(registry!).DoesNotContain("999");
  }

  // ==================== [AwaitPerspectiveSync] pipeline ====================

  /// <summary>
  /// [AwaitPerspectiveSync] with ALL named arguments (EventTypes, TimeoutMs, FireBehavior)
  /// on a receptor handling an IEvent must flow into both the registry metadata
  /// (ReceptorSyncAttributeInfo) and the dispatcher sync-await code. FireAlways must NOT
  /// emit the timeout-throw block.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAwaitPerspectiveSyncAllOptions_EmitsSyncMetadataAndAwaitCodeAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives.Sync;

      namespace MyApp.Receptors;

      public sealed class OrderPlaced : IEvent { }
      public sealed class OrderArchived : IEvent { }
      public class OrderPerspective { }

      [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderPlaced) }, TimeoutMs = 1234, FireBehavior = SyncFireBehavior.FireAlways)]
      public class ArchiveReceptor : IReceptor<OrderPlaced, OrderArchived> {
        public ValueTask<OrderArchived> HandleAsync(OrderPlaced message, CancellationToken ct = default)
          => ValueTask.FromResult(new OrderArchived());
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("ReceptorSyncAttributeInfo(PerspectiveType: typeof(global::MyApp.Receptors.OrderPerspective)");
    await Assert.That(registry!).Contains("EventTypes: new global::System.Type[] { typeof(global::MyApp.Receptors.OrderPlaced) }");
    await Assert.That(registry!).Contains("TimeoutMs: 1234");
    await Assert.That(registry!).Contains("global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireAlways");

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, DISPATCHER_FILE);
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher!).Contains("WaitForStreamAsync");
    await Assert.That(dispatcher!).Contains("FromMilliseconds(1234)");
    // FireAlways (1) suppresses the FireOnSuccess timeout-throw block
    await Assert.That(dispatcher!).DoesNotContain("SyncOutcome.TimedOut");
  }

  /// <summary>
  /// [AwaitPerspectiveSync] with only the perspective type must record EventTypes: null and
  /// TimeoutMs: -1 in the registry, while the dispatcher await code falls back to the 5000ms
  /// default and throws PerspectiveSyncTimeoutException (FireOnSuccess default behavior).
  /// Uses a VOID receptor so the sync-await code flows through VOID_SEND_ROUTING.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAwaitPerspectiveSyncDefaults_UsesFallbackTimeoutAndThrowsAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives.Sync;

      namespace MyApp.Receptors;

      public sealed class OrderPlaced : IEvent { }
      public class OrderPerspective { }

      [AwaitPerspectiveSync(typeof(OrderPerspective))]
      public class NotifyReceptor : IReceptor<OrderPlaced> {
        public ValueTask HandleAsync(OrderPlaced message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("EventTypes: null");
    await Assert.That(registry!).Contains("TimeoutMs: -1");
    await Assert.That(registry!).Contains("global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireOnSuccess");

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, DISPATCHER_FILE);
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher!).Contains("WaitForStreamAsync");
    await Assert.That(dispatcher!).Contains("FromMilliseconds(5000)");
    await Assert.That(dispatcher!).Contains("PerspectiveSyncTimeoutException");
  }

  /// <summary>
  /// [AwaitPerspectiveSync] on a receptor whose message is a COMMAND (not IEvent) must skip
  /// sync-await code generation entirely — perspectives only process events, so waiting
  /// would always time out. A marker comment is emitted instead.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAwaitPerspectiveSyncOnCommand_SkipsSyncAwaitCodeAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives.Sync;

      namespace MyApp.Receptors;

      public sealed class PlaceOrder : ICommand { }
      public sealed class OrderPlaced : IEvent { }
      public class OrderPerspective { }

      [AwaitPerspectiveSync(typeof(OrderPerspective))]
      public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
        public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken ct = default)
          => ValueTask.FromResult(new OrderPlaced());
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, DISPATCHER_FILE);
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher!).Contains("[AwaitPerspectiveSync] ignored - message is not an IEvent");
    await Assert.That(dispatcher!).DoesNotContain("WaitForStreamAsync");
  }

  /// <summary>
  /// Multiple [AwaitPerspectiveSync] attributes on one receptor must all appear in the
  /// registry array (comma-separated entries). Also exercises the FireOnEachEvent (2)
  /// mapping and the unknown-value fallback (7 maps to FireOnSuccess).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleAwaitPerspectiveSyncAttributes_EmitsAllEntriesAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives.Sync;

      namespace MyApp.Receptors;

      public sealed class OrderPlaced : IEvent { }
      public class PerspectiveA { }
      public class PerspectiveB { }
      public class PerspectiveC { }

      [AwaitPerspectiveSync(typeof(PerspectiveA))]
      [AwaitPerspectiveSync(typeof(PerspectiveB), FireBehavior = SyncFireBehavior.FireOnEachEvent)]
      [AwaitPerspectiveSync(typeof(PerspectiveC), FireBehavior = (SyncFireBehavior)7)]
      public class MultiSyncReceptor : IReceptor<OrderPlaced> {
        public ValueTask HandleAsync(OrderPlaced message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("typeof(global::MyApp.Receptors.PerspectiveA)");
    await Assert.That(registry!).Contains("typeof(global::MyApp.Receptors.PerspectiveB)");
    await Assert.That(registry!).Contains("typeof(global::MyApp.Receptors.PerspectiveC)");
    await Assert.That(registry!).Contains("global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireOnEachEvent");
    // Unknown FireBehavior value (7) falls back to FireOnSuccess
    await Assert.That(registry!).Contains("global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireOnSuccess");
    // Multiple entries -> comma-separated array elements
    await Assert.That(registry!).Contains("), new global::Whizbang.Core.Messaging.ReceptorSyncAttributeInfo(");
  }

  // ==================== Replay / idempotency attribute flags ====================

  /// <summary>
  /// The legacy [FireDuringReplay] attribute (matched by fully-qualified display name)
  /// must stamp FireDuringReplay: true in the registry. The attribute type is declared in
  /// the test source under the Whizbang.Core.Messaging namespace, matching how the
  /// generator resolves it by name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithFireDuringReplayAttribute_SetsFireDuringReplayTrueAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace Whizbang.Core.Messaging {
        [System.AttributeUsage(System.AttributeTargets.Class)]
        public sealed class FireDuringReplayAttribute : System.Attribute { }
      }

      namespace MyApp.Receptors {
        public sealed class CacheRefreshed : IEvent { }

        [Whizbang.Core.Messaging.FireDuringReplay]
        public class ReplayReceptor : IReceptor<CacheRefreshed> {
          public ValueTask HandleAsync(CacheRefreshed message, CancellationToken ct = default) => ValueTask.CompletedTask;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("FireDuringReplay: true");
    await Assert.That(registry!).Contains("IsIdempotent: false");
  }

  /// <summary>
  /// [ReceptorIdempotent(AlwaysFire = true)] must set BOTH FireDuringReplay (replay-safe)
  /// and IsIdempotent (double-fire guardrail bypass) to true.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithReceptorIdempotentAlwaysFire_SetsReplayAndIdempotentFlagsAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace MyApp.Receptors;

      public sealed class CacheRefreshed : IEvent { }

      [ReceptorIdempotent(AlwaysFire = true)]
      public class IdempotentReceptor : IReceptor<CacheRefreshed> {
        public ValueTask HandleAsync(CacheRefreshed message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("FireDuringReplay: true");
    await Assert.That(registry!).Contains("IsIdempotent: true");
  }

  /// <summary>
  /// Plain [ReceptorIdempotent] (AlwaysFire not set) must set IsIdempotent: true but leave
  /// FireDuringReplay: false — idempotent does not imply replay-fire.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithReceptorIdempotentDefault_SetsIdempotentOnlyAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace MyApp.Receptors;

      public sealed class CacheRefreshed : IEvent { }

      [ReceptorIdempotent]
      public class IdempotentReceptor : IReceptor<CacheRefreshed> {
        public ValueTask HandleAsync(CacheRefreshed message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("FireDuringReplay: false");
    await Assert.That(registry!).Contains("IsIdempotent: true");
  }

  // ==================== Traced void registry snippet ====================

  /// <summary>
  /// [WhizbangTrace] on a VOID receptor must select the TRACED VOID registry snippet —
  /// begin/end handler trace calls with the handler-count and is-explicit placeholders
  /// fully replaced.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangTraceOnVoidReceptor_UsesTracedVoidRegistrySnippetAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Tracing;

      namespace MyApp.Receptors;

      public sealed class ItemShipped : IEvent { }

      [WhizbangTrace]
      public class TracedVoidReceptor : IReceptor<ItemShipped> {
        public ValueTask HandleAsync(ItemShipped message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("BeginHandlerTrace");
    await Assert.That(registry!).Contains("EndHandlerTrace");
    // Placeholders must be fully replaced (handler count / explicit flag)
    await Assert.That(registry!).DoesNotContain("__HANDLER_COUNT__");
    await Assert.That(registry!).DoesNotContain("__IS_EXPLICIT__");
    // Void receptor -> no result unwrapping in the invoke delegate for this receptor
    await Assert.That(registry!).Contains("TracedVoidReceptor");
  }

  // ==================== [DefaultRouting] on sync receptor ====================

  /// <summary>
  /// [DefaultRouting(DispatchModes.Local)] on a SYNC receptor must resolve the enum value
  /// back to its fully-qualified member name in the generated routing lookup.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDefaultRoutingOnSyncReceptor_EmitsFullyQualifiedEnumMemberAsync() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Dispatch;

      namespace MyApp.Receptors;

      public sealed class CacheInvalidated : ICommand { }

      [DefaultRouting(DispatchModes.Local)]
      public class CacheSyncReceptor : ISyncReceptor<CacheInvalidated> {
        public void Handle(CacheInvalidated message) { }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, DISPATCHER_FILE);
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.CacheInvalidated)");
    await Assert.That(dispatcher!).Contains("return global::Whizbang.Core.Dispatch.DispatchModes.Local;");
  }

  // ==================== Polymorphic expansion: unresolvable metadata name ====================

  /// <summary>
  /// A NON-SEALED message class NESTED inside another type is polymorphic, but its dotted
  /// fully-qualified name cannot be resolved via GetTypeByMetadataName (metadata uses '+'
  /// for nesting). The subtype search must return empty instead of crashing, and the
  /// receptor must still be registered against the nested type itself.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedNonSealedMessageType_SkipsSubtypeExpansionGracefullyAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp.Receptors;

      public static class Contracts {
        public class ThingHappened : IEvent { }
      }

      public class ThingReceptor : IReceptor<Contracts.ThingHappened> {
        public ValueTask HandleAsync(Contracts.ThingHappened message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("typeof(global::MyApp.Receptors.Contracts.ThingHappened)");
    await Assert.That(registry!).Contains("ThingReceptor");
  }

  // ==================== Tuple with array element cascade ====================

  /// <summary>
  /// A tuple response containing an ARRAY element must extract the array's element type
  /// (not the array type) for the outbox cascade type-switch.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTupleContainingArrayElement_ExtractsElementTypeForCascadeAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp.Receptors;

      public sealed class ClearCart : ICommand { }
      public sealed class ItemRemoved : IEvent { }
      public sealed class CartCleared : IEvent { }

      public class ClearCartReceptor : IReceptor<ClearCart, (ItemRemoved[], CartCleared)> {
        public ValueTask<(ItemRemoved[], CartCleared)> HandleAsync(ClearCart message, CancellationToken ct = default)
          => ValueTask.FromResult((new ItemRemoved[0], new CartCleared()));
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, DISPATCHER_FILE);
    await Assert.That(dispatcher).IsNotNull();
    // Array element type cascaded, not the array type itself
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.ItemRemoved)");
    await Assert.That(dispatcher!).Contains("typeof(global::MyApp.Receptors.CartCleared)");
    await Assert.That(dispatcher!).DoesNotContain("typeof(global::MyApp.Receptors.ItemRemoved[])");
  }

  // ==================== Non-test assembly early exit ====================

  /// <summary>
  /// In a NON-test assembly with no receptors AND no perspectives, the generator must
  /// report WHIZ002 and skip generation entirely — no dispatcher, registry, or
  /// registration sources. (Test-named assemblies continue generating for runtime
  /// receptor registration; that path is covered by the main suite via "TestAssembly".)
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NonTestAssemblyWithNoHandlers_ReportsWhiz002AndSkipsGenerationAsync() {
    const string source = """
      namespace ConsumerApp;

      public class PlainService { }
      """;

    var result = _runGeneratorWithAssemblyName(source, "ConsumerApp.Api");

    await Assert.That(result.Diagnostics).Contains(d => d.Id == "WHIZ002");
    await Assert.That(result.GeneratedTrees).IsEmpty();
  }

  // ==================== Helpers ====================

  /// <summary>
  /// Runs the ReceptorDiscoveryGenerator against a compilation with a caller-controlled
  /// assembly name. GeneratorTestHelper always uses "TestAssembly", which triggers the
  /// test-project detection path — this helper lets tests exercise the production path.
  /// </summary>
  [RequiresAssemblyFiles()]
  private static GeneratorDriverRunResult _runGeneratorWithAssemblyName(string source, string assemblyName) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new List<MetadataReference> {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("netstandard").Location),
      MetadataReference.CreateFromFile(typeof(Whizbang.Core.IEvent).Assembly.Location)
    };

    var compilation = CSharpCompilation.Create(
        assemblyName: assemblyName,
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    var driver = CSharpGeneratorDriver.Create(new ReceptorDiscoveryGenerator());
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
    return driver.GetRunResult();
  }
}
