using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Covers the saga branch of <see cref="ReceptorDiscoveryGenerator"/>: a [Saga] class gets
/// recovery receptors emitted for it so completion is driven from the store rather than
/// from a single pod's in-memory tracker.
/// </summary>
/// <remarks>
/// The attribute is declared inline in each source rather than referenced from
/// Whizbang.Sagas.Contracts. ForAttributeWithMetadataName matches on metadata name, so a
/// local declaration in the right namespace is what the generator keys on — and it keeps
/// these tests from needing a project reference the generator itself does not have.
/// </remarks>
[Category("SourceGenerators")]
[Category("ReceptorDiscovery")]
public class SagaRecoveryReceptorDiscoveryTests {

  private const string SAGA_ATTRIBUTE_DECL = @"
namespace Whizbang.Sagas {
  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class SagaAttribute : System.Attribute {
    public SagaAttribute(string sagaName) { SagaName = sagaName; }
    public string SagaName { get; }
    public bool GenerateService { get; init; } = true;
  }

  [System.AttributeUsage(System.AttributeTargets.Class)]
  public sealed class SagaAttribute<TBase> : System.Attribute where TBase : class {
    public SagaAttribute(string sagaName) { SagaName = sagaName; }
    public string SagaName { get; }
    public bool GenerateService { get; init; } = true;
  }
}
";

  private static string _source(string sagaDeclaration) => SAGA_ATTRIBUTE_DECL + @"
namespace MyApp.Sagas;

" + sagaDeclaration;

  [Test]
  public async Task PublicSaga_GetsRecoveryReceptorsAsync() {
    // The per-item terminals and the watchdog tick are what drive SagaCompletedEvent from
    // the store; without them completion depends on one pod's in-memory tracker.
    var source = _source(@"
[global::Whizbang.Sagas.Saga(""orders"")]
public partial class OrderSaga {
  public sealed record ItemCompletedEvent;
  public sealed record ItemFailedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("SagaItemCompletedRecoveryHandler");
  }

  [Test]
  public async Task SagaDeclaredWithTheGenericAttribute_GetsTheSameRecoveryReceptorsAsync() {
    // [Saga<TBase>("name")] is the form a consumer uses to pick the base its nine event classes
    // derive from. It is a SEPARATE attribute — a different metadata name — so the generator
    // registers a second discovery pipeline for it. Nothing matched that pipeline, which means a
    // saga written the generic way got no recovery receptors at all and its completion fell back
    // to one pod's in-memory tracker.
    var source = _source(@"
namespace MyApp.Contracts { public class AppSagaEventBase { } }

[global::Whizbang.Sagas.Saga<global::MyApp.Contracts.AppSagaEventBase>(""orders"")]
public partial class OrderSaga {
  public sealed record ItemCompletedEvent;
  public sealed record ItemFailedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("SagaItemCompletedRecoveryHandler")
      .Because("which attribute overload declared the saga must not change whether its completion "
             + "is recoverable");
  }

  [Test]
  public async Task TheWatchdogTickReceptor_IsGeneratedWhenTheTickTypeIsReferencedAsync() {
    // The watchdog handler is the safety net: it is what drives SagaCompletedEvent when per-item
    // terminal events were dropped before the right pod's tracker saw them. Unlike the per-item
    // handlers, its message is the framework's own tick type rather than a nested generated one,
    // so it is the only receptor here that depends on a type being RESOLVABLE in the compilation.
    // When it is not, the generator skips that one shape and emits the rest — which reads exactly
    // like a working generator right up until a saga strands and nothing wakes to recover it.
    // The tick type is declared HERE rather than in the shared surface, because the companion
    // test asserts the handler is omitted without it — putting it in the surface would quietly
    // disarm that one.
    var source = SAGA_ATTRIBUTE_DECL + @"
namespace Whizbang.Sagas {
  public sealed class SagaCompletionWatchdogTickEvent : global::Whizbang.Core.IEvent {
    public System.Guid SagaId { get; set; }
  }
}

namespace MyApp.Sagas;

[global::Whizbang.Sagas.Saga(""orders"")]
public partial class OrderSaga {
  public sealed record ItemCompletedEvent;
  public sealed record ItemFailedEvent;
}
";

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher!).Contains("SagaCompletionWatchdogTickHandler")
      .Because("without the tick handler a stranded saga has nothing scheduled to recover it, and "
             + "the other receptors being present makes that look like a working generator");
    await Assert.That(dispatcher!).Contains("SagaCompletionWatchdogTickEvent")
      .Because("the handler has to be bound to the framework tick type it actually receives");
  }

  [Test]
  public async Task GenericSaga_IsSkippedAsync() {
    // A generic saga has no single closed type to emit handlers against.
    var source = _source(@"
[global::Whizbang.Sagas.Saga(""orders"")]
public partial class OrderSaga<T> where T : class {
  public sealed record ItemCompletedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    if (dispatcher is not null) {
      await Assert.That(dispatcher).DoesNotContain("SagaItemCompletedRecoveryHandler");
    }
  }

  [Test]
  public async Task InternalSaga_IsSkippedAsync() {
    // The emitted handlers are public types referencing the saga, so a non-public saga
    // would produce code that cannot compile.
    var source = _source(@"
[global::Whizbang.Sagas.Saga(""orders"")]
internal partial class OrderSaga {
  public sealed record ItemCompletedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    if (dispatcher is not null) {
      await Assert.That(dispatcher).DoesNotContain("SagaItemCompletedRecoveryHandler");
    }
  }

  [Test]
  public async Task SagaNestedInANonPublicType_IsSkippedAsync() {
    // The accessibility walk goes up the containing chain, not just the saga itself.
    var source = _source(@"
internal static partial class Holder {
  [global::Whizbang.Sagas.Saga(""orders"")]
  public partial class OrderSaga {
    public sealed record ItemCompletedEvent;
  }
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    if (dispatcher is not null) {
      await Assert.That(dispatcher).DoesNotContain("SagaItemCompletedRecoveryHandler");
    }
  }

  [Test]
  public async Task SagaWithGenerateServiceFalse_IsSkippedAsync() {
    // The recovery receptors take the generated service as a constructor dependency, so
    // suppressing the service must suppress them too rather than emit uncompilable code.
    var source = _source(@"
[global::Whizbang.Sagas.Saga(""orders"", GenerateService = false)]
public partial class OrderSaga {
  public sealed record ItemCompletedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    if (dispatcher is not null) {
      await Assert.That(dispatcher).DoesNotContain("SagaItemCompletedRecoveryHandler");
    }
  }

  [Test]
  public async Task SagaWithGenerateServiceTrue_StillEmitsAsync() {
    var source = _source(@"
[global::Whizbang.Sagas.Saga(""orders"", GenerateService = true)]
public partial class OrderSaga {
  public sealed record ItemCompletedEvent;
  public sealed record ItemFailedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("SagaItemCompletedRecoveryHandler");
  }

  [Test]
  public async Task WithoutTheFrameworkTickType_TheWatchdogHandlerIsOmittedAsync() {
    // The watchdog receptor is keyed on the framework's own tick event. Without
    // Whizbang.Sagas referenced there is no such type, so that one shape is skipped while
    // the per-item terminals still emit.
    var source = _source(@"
[global::Whizbang.Sagas.Saga(""orders"")]
public partial class OrderSaga {
  public sealed record ItemCompletedEvent;
  public sealed record ItemFailedEvent;
}
");

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).DoesNotContain("SagaCompletionWatchdogTickHandler");
  }
}
