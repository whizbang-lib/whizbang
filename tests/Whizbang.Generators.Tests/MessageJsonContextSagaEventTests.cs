using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests that <see cref="MessageJsonContextGenerator"/> synthesizes JSON metadata for the nine event
/// classes the saga generator emits into a <c>[Saga]</c>-marked partial class.
///
/// <para>Source generators never observe each other's output: the saga generator emits the nested
/// event classes, and this generator — reading the same pre-generation compilation — cannot see them.
/// Without synthesis the consumer's own <c>MessageJsonContext</c> holds no <c>JsonTypeInfo</c> for its
/// own saga events, and the first <c>InitiateSagaAsync</c> fails with
/// <c>NotSupportedException: JsonTypeInfo metadata for type '…+InitiatedEvent' was not provided</c>.</para>
/// </summary>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
[Category("Saga")]
public class MessageJsonContextSagaEventTests {

  /// <summary>
  /// The saga attribute surface plus the default event base, declared inline so the test compiles
  /// without a reference to Whizbang.Sagas. <c>ForAttributeWithMetadataName</c> matches on metadata
  /// name, so an in-source declaration is indistinguishable from the shipped one.
  /// </summary>
  private const string SAGA_SURFACE = @"
using System;
using Whizbang.Core;

namespace Whizbang.Sagas {
  public enum SagaStatus { Pending = 0, Running = 1, Completed = 2 }
  public enum SagaItemState { Pending = 0, Running = 1, Completed = 2 }

  public class SagaEventBase : IEvent {
    public Guid MessageId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? OperationName { get; set; }
  }

  [AttributeUsage(AttributeTargets.Class)]
  public sealed class SagaAttribute : Attribute {
    public SagaAttribute(string name) { Name = name; }
    public string Name { get; }
    public bool IncludeHooks { get; set; } = true;
    public bool GenerateService { get; set; } = true;
  }

  [AttributeUsage(AttributeTargets.Class)]
  public sealed class SagaAttribute<TEventBase> : Attribute where TEventBase : class, IEvent, new() {
    public SagaAttribute(string name) { Name = name; }
    public string Name { get; }
    public bool IncludeHooks { get; set; } = true;
    public bool GenerateService { get; set; } = true;
  }
}
";

  private static string _withSagaSurface(string source) => SAGA_SURFACE + source;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSagaDeclaration_GeneratesJsonTypeInfoForEveryEmittedEventAsync() {
    var source = _withSagaSurface(@"
namespace ConsumerApp.Sagas;

[Whizbang.Sagas.Saga(""landing"")]
public partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    string[] emittedEvents = [
      "InitiatedEvent", "ItemsDispatchedEvent", "ItemStartedEvent", "ItemCompletedEvent",
      "ItemFailedEvent", "CompletedEvent", "ResetEvent", "HookStartedEvent", "HookCompletedEvent"
    ];

    foreach (var eventName in emittedEvents) {
      await Assert.That(code!).Contains($"global::ConsumerApp.Sagas.LandingSaga.{eventName}")
        .Because($"Every saga event the generator emits needs JsonTypeInfo, or publishing {eventName} throws NotSupportedException at serialization.");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSagaDeclaration_GeneratesEnvelopeFactoryForSagaEventsAsync() {
    var source = _withSagaSurface(@"
namespace ConsumerApp.Sagas;

[Whizbang.Sagas.Saga(""landing"")]
public partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The transport consumer side receives MessageEnvelope<T> JSON and resolves the concrete generic
    // via JsonTypeInfo — without the envelope wrapper the publish succeeds and the receive fails.
    await Assert.That(code!).Contains("MessageEnvelope<global::ConsumerApp.Sagas.LandingSaga.InitiatedEvent>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSagaDeclaration_RegistersSagaEventsForPolymorphicDispatchAsync() {
    var source = _withSagaSurface(@"
namespace ConsumerApp.Sagas;

[Whizbang.Sagas.Saga(""landing"")]
public partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    var initializer = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializer).IsNotNull();

    await Assert.That(initializer!).Contains("global::ConsumerApp.Sagas.LandingSaga.InitiatedEvent")
      .Because("Saga events must register as IEvent derived types, or MessageEnvelope<IEvent> reads of a saga stream cannot resolve them.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSagaDeclaration_IncludesEventBasePropertiesAsync() {
    var source = _withSagaSurface(@"
namespace ConsumerApp.Sagas;

[Whizbang.Sagas.Saga(""landing"")]
public partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Inherited SagaEventBase members carry the correlation/causation chain — dropping them would
    // serialize a saga event that loses its place in the causal graph.
    await Assert.That(code!).Contains("\"OccurredAt\"");
    await Assert.That(code!).Contains("\"CorrelationId\"");
    // Declared members of the emitted event class.
    await Assert.That(code!).Contains("\"ItemIdentifiers\"");
    await Assert.That(code!).Contains("\"TotalItems\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCustomEventBase_UsesThatBasesPropertiesAsync() {
    var source = _withSagaSurface(@"
using System;
using Whizbang.Core;

namespace ConsumerApp.Sagas;

public class TenantSagaEventBase : IEvent {
  public Guid MessageId { get; set; }
  public DateTimeOffset OccurredAt { get; set; }
  public string TenantSlug { get; set; } = """";
}

[Whizbang.Sagas.Saga<TenantSagaEventBase>(""landing"")]
public partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("\"TenantSlug\"")
      .Because("[Saga<TBase>] events inherit the consumer's own base — its properties must reach the wire.");
    await Assert.That(code!).Contains("global::ConsumerApp.Sagas.LandingSaga.InitiatedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithHooksDisabled_OmitsHookEventsAsync() {
    var source = _withSagaSurface(@"
namespace ConsumerApp.Sagas;

[Whizbang.Sagas.Saga(""landing"", IncludeHooks = false)]
public partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("global::ConsumerApp.Sagas.LandingSaga.InitiatedEvent");
    await Assert.That(code!).DoesNotContain("global::ConsumerApp.Sagas.LandingSaga.HookStartedEvent")
      .Because("IncludeHooks = false stops the saga generator emitting the hook events, so metadata for them would not compile.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonPublicSaga_SkipsSynthesisAsync() {
    var source = _withSagaSurface(@"
namespace ConsumerApp.Sagas;

[Whizbang.Sagas.Saga(""landing"")]
internal partial class LandingSaga { }
");

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("LandingSaga.InitiatedEvent")
      .Because("The generated context is public — referencing an internal saga's nested types would not compile.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoSagas_GeneratesUnchangedContextAsync() {
    const string source = @"
using Whizbang.Core;

namespace ConsumerApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("global::ConsumerApp.Commands.CreateOrder");
  }
}
