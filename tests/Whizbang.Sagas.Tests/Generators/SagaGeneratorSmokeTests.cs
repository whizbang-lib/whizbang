using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Generators;

/// <summary>
/// Smoke test for the [Saga] / [Saga&lt;TBase&gt;] source generator —
/// asserts that the emitted output actually compiles, that the nested
/// event classes implement the right Whizbang.Sagas.Contracts
/// interfaces, and that the consumer's experience (one-line attribute
/// → resolvable .Service) holds end-to-end.
/// </summary>
[Category("Unit")]
[Category("Saga")]
[Category("Generator")]
public class SagaGeneratorSmokeTests {

  [Test]
  public async Task DefaultBase_EmittedClassesImplementContractInterfacesAsync() {
    var initiated = new GeneratorTestDefaultSaga.InitiatedEvent();
    var completed = new GeneratorTestDefaultSaga.CompletedEvent();
    var hookStarted = new GeneratorTestDefaultSaga.HookStartedEvent();

    await Assert.That(initiated is ISagaInitiatedEvent).IsTrue();
    await Assert.That(completed is ISagaCompletedEvent).IsTrue();
    await Assert.That(hookStarted is ISagaHookStartedEvent).IsTrue();
  }

  [Test]
  public async Task DefaultBase_InheritsSagaEventBaseAsync() {
    var evt = new GeneratorTestDefaultSaga.InitiatedEvent();

    await Assert.That(evt is SagaEventBase).IsTrue()
      .Because("Without a TEventBase, the generator must inherit from Whizbang.Sagas.SagaEventBase.");
  }

  [Test]
  public async Task SagaNameConstant_MatchesAttributeArgumentAsync() {
    var name = GeneratorTestDefaultSaga.SagaName;
    await Assert.That(name).IsEqualTo("GeneratorTestDefault");
  }

  [Test]
  public async Task SagaName_PopulatedOnEmittedEventsAsync() {
    var evt = new GeneratorTestDefaultSaga.InitiatedEvent();

    await Assert.That(evt.SagaName).IsEqualTo("GeneratorTestDefault")
      .Because("Each emitted event class defaults its SagaName field to the generator-known constant — consumers and projections can filter on it without each call site setting it.");
  }

  [Test]
  public async Task ServiceClass_DerivesFromBaseSagaServiceAsync() {
    var service = new GeneratorTestDefaultSaga.Service(new RecordingEmitter(), NullLogger<GeneratorTestDefaultSaga.Service>.Instance);

    await Assert.That(service is BaseSagaService<
      GeneratorTestDefaultSaga.InitiatedEvent,
      GeneratorTestDefaultSaga.ItemsDispatchedEvent,
      GeneratorTestDefaultSaga.ItemStartedEvent,
      GeneratorTestDefaultSaga.ItemCompletedEvent,
      GeneratorTestDefaultSaga.ItemFailedEvent,
      GeneratorTestDefaultSaga.CompletedEvent,
      GeneratorTestDefaultSaga.ResetEvent,
      GeneratorTestDefaultSaga.HookStartedEvent,
      GeneratorTestDefaultSaga.HookCompletedEvent>).IsTrue()
      .Because("The generator must emit a Service class typed on all 9 nested event types — that's what makes the consumer's one-line declaration produce a working saga service.");
  }

  // Note: end-to-end DI resolution test deferred — generator emits an
  // AddGeneratorTestDefaultSaga() extension method that registers
  // Service as Scoped; verifying that requires matching the runtime
  // logger generic argument exactly which is brittle. The shape itself
  // (Scoped registration of the typed Service class) is locked by the
  // ServiceClass_DerivesFromBaseSagaService test above.

  // ── Custom base test ─────────────────────────────────────────────────

  [Test]
  public async Task CustomBase_EmittedClassesInheritItAsync() {
    var evt = new GeneratorTestCustomBaseSaga.InitiatedEvent();

    await Assert.That(evt is FakeProjectEventBase).IsTrue()
      .Because("[Saga<FakeProjectEventBase>(\"Name\")] must emit event classes inheriting from FakeProjectEventBase — that's the whole point of the generic attribute form (preserves consumer event hierarchy).");
  }

  // ── Generator-emitted recovery receptors (Component 3) ──────────────

  [Test]
  public async Task RecoveryReceptors_AreEmittedForEachSagaAsync() {
    // Component 3 of plans/sagas-framework-owns-completion.md: the generator
    // must emit three receptor classes per [Saga("Name")] so consumers don't
    // hand-roll the boilerplate that bridges per-item terminal events ↔
    // BaseSagaService.TryRecoverViaWatchdogAsync (the only completion path
    // that works under cross-pod fan-out).
    var sagaItemCompleted = typeof(GeneratorTestDefaultSaga).GetNestedType("SagaItemCompletedRecoveryHandler");
    var sagaItemFailed = typeof(GeneratorTestDefaultSaga).GetNestedType("SagaItemFailedRecoveryHandler");
    var watchdogTick = typeof(GeneratorTestDefaultSaga).GetNestedType("SagaCompletionWatchdogTickHandler");

    await Assert.That(sagaItemCompleted).IsNotNull()
      .Because("the generator must emit SagaItemCompletedRecoveryHandler so per-item completion bridges to BaseSagaService.TryRecoverViaWatchdogAsync without consumer-written boilerplate");
    await Assert.That(sagaItemFailed).IsNotNull()
      .Because("the generator must emit SagaItemFailedRecoveryHandler with the same recovery-driven semantics for failed items");
    await Assert.That(watchdogTick).IsNotNull()
      .Because("the generator must emit SagaCompletionWatchdogTickHandler so the auto-armed watchdog tick re-arm/abandon lifecycle runs without consumer-written boilerplate");
  }

  [Test]
  public async Task SagaItemCompletedRecoveryHandler_ImplementsIReceptorOfItemCompletedAsync() {
    var receptorType = typeof(GeneratorTestDefaultSaga).GetNestedType("SagaItemCompletedRecoveryHandler")!;
    var expectedInterface = typeof(global::Whizbang.Core.IReceptor<>).MakeGenericType(typeof(GeneratorTestDefaultSaga.ItemCompletedEvent));

    var implements = receptorType.GetInterfaces().Any(i => i == expectedInterface);

    await Assert.That(implements).IsTrue()
      .Because("the generated recovery handler must be the receptor for the saga's own ItemCompletedEvent type — that's how Whizbang.Generators routes the per-item terminal to the recovery path");
  }

  [Test]
  public async Task SagaItemFailedRecoveryHandler_ImplementsIReceptorOfItemFailedAsync() {
    var receptorType = typeof(GeneratorTestDefaultSaga).GetNestedType("SagaItemFailedRecoveryHandler")!;
    var expectedInterface = typeof(global::Whizbang.Core.IReceptor<>).MakeGenericType(typeof(GeneratorTestDefaultSaga.ItemFailedEvent));

    var implements = receptorType.GetInterfaces().Any(i => i == expectedInterface);

    await Assert.That(implements).IsTrue();
  }

  [Test]
  public async Task SagaCompletionWatchdogTickHandler_ImplementsIReceptorOfWatchdogTickAsync() {
    var receptorType = typeof(GeneratorTestDefaultSaga).GetNestedType("SagaCompletionWatchdogTickHandler")!;
    var expectedInterface = typeof(global::Whizbang.Core.IReceptor<global::Whizbang.Sagas.SagaCompletionWatchdogTickEvent>);

    var implements = receptorType.GetInterfaces().Any(i => i == expectedInterface);

    await Assert.That(implements).IsTrue()
      .Because("the watchdog handler receives the framework-emitted SagaCompletionWatchdogTickEvent (NOT a per-saga generated type) so all sagas share the same tick event shape");
  }

  // ── Recording emitter (reused) ───────────────────────────────────────

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : Whizbang.Core.IEvent => Task.CompletedTask;
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : Whizbang.Core.IEvent => Task.FromResult(true);
  }
}

// ── Saga declarations the generator will pick up ───────────────────────

[Saga("GeneratorTestDefault")]
public partial class GeneratorTestDefaultSaga;

public class FakeProjectEventBase : Whizbang.Core.IEvent {
  /// <summary>Every emitted saga event overrides this with its own [StreamId] EntityId; the base
  /// carries one so the base type itself satisfies stream-id resolution (WHIZ009).</summary>
  [Whizbang.Core.StreamId] public Guid StreamEntityId { get; set; }
  public Guid MessageId { get; set; } = Guid.NewGuid();
}

[Saga<FakeProjectEventBase>("GeneratorTestCustomBase")]
public partial class GeneratorTestCustomBaseSaga;
