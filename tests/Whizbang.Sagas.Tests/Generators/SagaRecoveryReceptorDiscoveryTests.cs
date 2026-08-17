using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Generators;

/// <summary>
/// The saga generator emits three recovery receptors into every <c>[Saga]</c>-marked class. They are
/// the only thing that bridges per-item terminal events and the auto-armed watchdog tick to
/// <c>BaseSagaService.TryRecoverViaWatchdogAsync</c> — the completion path that works under
/// cross-pod fan-out.
///
/// <para><c>ReceptorDiscoveryGenerator</c> discovers receptors syntactically, and those classes do
/// not exist in the compilation it reads: the saga generator produces them from the same input, and
/// generators do not observe each other's output. So in a consumer assembly they are neither
/// DI-registered (the <c>RECEPTOR_REGISTRATIONS</c> region of <c>AddReceptors()</c>) nor routed (the
/// <c>RECEPTOR_ROUTING</c> region of the generated registry) — the saga starts, dispatches its
/// items, and silently never completes.</para>
///
/// <para>Existing saga generator tests reflect over these types' shape, which passes whether or not
/// anything can ever invoke them. These assert the registration and routing that actually make them
/// run. This project loads both generators, exactly as a saga consumer's project does.</para>
/// </summary>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
[Category("Unit")]
[Category("Saga")]
[Category("Generator")]
public class SagaRecoveryReceptorDiscoveryTests {

  /// <summary>
  /// Minimal container holding what the generated receptors need to be constructed: the saga
  /// <c>Service</c> they take as a dependency, plus that service's own dependencies.
  /// </summary>
  private static ServiceProvider _buildProvider() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ISagaEventEmitter, NoOpSagaEventEmitter>();
    services.AddGeneratorTestDefaultSaga();
    services.AddGeneratorTestCustomBaseSaga();
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddReceptors(services);
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddWhizbangReceptorRegistry(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task ItemCompletedRecoveryHandler_IsRoutedForItsSagaEventAsync() {
    using var provider = _buildProvider();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    var receptors = registry.GetReceptorsFor(
      typeof(GeneratorTestDefaultSaga.ItemCompletedEvent),
      LifecycleStage.PostAllPerspectivesInline);

    await Assert.That(receptors).IsNotEmpty()
      .Because("Nothing routes ItemCompletedEvent to the generated recovery handler, so the last item completing across pods never drives SagaCompletedEvent — the saga hangs with every item done.");
  }

  [Test]
  public async Task ItemFailedRecoveryHandler_IsRoutedForItsSagaEventAsync() {
    using var provider = _buildProvider();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    var receptors = registry.GetReceptorsFor(
      typeof(GeneratorTestDefaultSaga.ItemFailedEvent),
      LifecycleStage.PostAllPerspectivesInline);

    await Assert.That(receptors).IsNotEmpty()
      .Because("Failed items are terminal too — they must nudge recovery or a saga whose last item fails never completes.");
  }

  [Test]
  public async Task WatchdogTickHandler_IsRoutedForTheFrameworkTickEventAsync() {
    using var provider = _buildProvider();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    // The watchdog handler carries no [FireAt], so it lands on the default stage.
    var stages = Enum.GetValues<LifecycleStage>();
    var routedAtAnyStage = stages.Any(stage =>
      registry.GetReceptorsFor(typeof(SagaCompletionWatchdogTickEvent), stage).Count > 0);

    await Assert.That(routedAtAnyStage).IsTrue()
      .Because("The watchdog tick is the safety net that re-arms with backoff and eventually abandons; unrouted, a stranded saga is never reconciled at all.");
  }

  [Test]
  public async Task RecoveryHandlers_AreResolvableFromTheContainerAsync() {
    using var provider = _buildProvider();

    var handler = provider.GetService<IReceptor<GeneratorTestDefaultSaga.ItemCompletedEvent>>();

    await Assert.That(handler).IsNotNull()
      .Because("AddReceptors() is what makes a receptor injectable; the generated saga handlers must appear there like any hand-written receptor.");
  }

  [Test]
  public async Task EverySagaInTheAssembly_GetsItsOwnRoutedHandlersAsync() {
    using var provider = _buildProvider();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    var defaultSaga = registry.GetReceptorsFor(
      typeof(GeneratorTestDefaultSaga.ItemCompletedEvent), LifecycleStage.PostAllPerspectivesInline);
    var customBaseSaga = registry.GetReceptorsFor(
      typeof(GeneratorTestCustomBaseSaga.ItemCompletedEvent), LifecycleStage.PostAllPerspectivesInline);

    await Assert.That(defaultSaga).IsNotEmpty();
    await Assert.That(customBaseSaga).IsNotEmpty()
      .Because("Each saga's handlers are typed on that saga's own event classes — routing one saga must not stand in for another.");
  }

  /// <summary>
  /// Drift guard. <c>ReceptorDiscoveryGenerator</c> describes these receptors from a compile-time
  /// shape table because it cannot see the classes the saga generator emits, and nothing in the
  /// compiler couples the two. This reflects over the classes that were actually emitted and demands
  /// the routed set match them exactly — by receptor class name, by handled message type, and by
  /// lifecycle stage. Rename a receptor, retarget it, or move its <c>[FireAt]</c> without updating
  /// the table and this test breaks instead of a consumer's saga silently stalling.
  /// </summary>
  [Test]
  public async Task RoutedHandlers_MatchTheEmittedRecoveryReceptorsAsync() {
    using var provider = _buildProvider();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    // What the saga generator actually emitted, discovered by reflection.
    var emitted = typeof(GeneratorTestDefaultSaga).GetNestedTypes()
      .Where(t => t.Name.Contains("Recovery", StringComparison.Ordinal)
               || t.Name.Contains("Watchdog", StringComparison.Ordinal))
      .ToArray();

    await Assert.That(emitted.Length).IsEqualTo(3)
      .Because("The shape table describes exactly three recovery receptors; a fourth emitted one would go unrouted.");

    foreach (var receptorType in emitted) {
      // The one IReceptor<T> interface each handler implements names the message it handles.
      var messageType = receptorType.GetInterfaces()
        .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReceptor<>))
        .GetGenericArguments()[0];

      // The stages the emitted class actually declares. A wrong message type or receptor name in the
      // shape table cannot get this far — it fails to compile the generated registrations. What CAN
      // compile and still be wrong is a stage that drifted, so compare stages exactly rather than
      // settling for "routed somewhere".
      var declaredStages = receptorType
        .GetCustomAttributes(typeof(FireAtAttribute), inherit: false)
        .Cast<FireAtAttribute>()
        .Select(a => a.Stage)
        .ToArray();

      var routedStages = Enum.GetValues<LifecycleStage>()
        .Where(stage => registry.GetReceptorsFor(messageType, stage)
          .Any(info => info.ReceptorId.Contains(receptorType.Name, StringComparison.Ordinal)))
        .ToArray();

      await Assert.That(routedStages).IsNotEmpty()
        .Because($"{receptorType.Name} was emitted handling {messageType.Name}, but nothing routes that pairing — the shape table has drifted from SagaGenerator.");

      if (declaredStages.Length > 0) {
        await Assert.That(routedStages).IsEquivalentTo(declaredStages)
          .Because($"{receptorType.Name} declares [FireAt({string.Join(", ", declaredStages)})]; routing it at a different stage runs the recovery bridge at the wrong point in the lifecycle.");
      }
    }
  }

  /// <summary>Emitter that records nothing; these tests assert routing, not saga behaviour.</summary>
  private sealed class NoOpSagaEventEmitter : ISagaEventEmitter {
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent => Task.CompletedTask;
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent => Task.FromResult(true);
  }
}
