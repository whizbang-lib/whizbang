using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Sagas.Services;
using Whizbang.Sagas.Tests.Generators;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// A delivered watchdog tick reaches the saga that armed it, through the real registry and the real
/// receptor invoker, with hand-written and <c>[Saga]</c>-declared sagas in the same host.
/// </summary>
/// <remarks>
/// <para>
/// The defect this locks lived in delivery, not in the recovery logic: a hand-written saga armed a
/// tick, the transport delivered it to the inbox on time, and at the stage the inbox invokes there
/// was no receptor for it, so it was discarded. Unit tests of the recovery method passed throughout,
/// because they called the method directly. This drives the tick through registration at startup and
/// invocation at the inbox stage — the two steps that were missing.
/// </para>
/// <para>
/// The host also carries <c>[Saga]</c>-declared sagas with their generated tick receivers, as a real
/// consumer can. Each tick must reach exactly one receiver: two would re-arm it twice.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/Services/SagaWatchdogTickRouterRegistrar.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/Services/SagaWatchdogTickRouter.cs</code-under-test>
[Category("Integration")]
[Category("Saga")]
public class SagaWatchdogTickDeliveryIntegrationTests {
  private const string HAND_WRITTEN = "HandWrittenImport";

  private static ServiceProvider _host(RecordingParticipant handWritten) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ISagaEventEmitter, NoOpEmitter>();
    // [Saga]-declared sagas, each with its own generated tick receiver.
    services.AddGeneratorTestDefaultSaga();
    services.AddGeneratorTestCustomBaseSaga();
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddReceptors(services);
    global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddWhizbangReceptorRegistry(services);
    services.AddWhizbangSagas();
    // The hand-written saga, as AddSagaService exposes one to the router.
    services.AddScoped<ISagaWatchdogParticipant>(_ => handWritten);
    return services.BuildServiceProvider();
  }

  private static async Task _startTheRouterRegistrarAsync(IServiceProvider provider) {
    var registrar = provider.GetServices<IHostedService>().OfType<SagaWatchdogTickRouterRegistrar>().Single();
    await registrar.StartAsync(CancellationToken.None);
  }

  private static async Task _deliverAsync(IServiceProvider provider, string sagaName, LifecycleStage stage) {
    await using var scope = provider.CreateAsyncScope();
    var invoker = new ReceptorInvoker(provider.GetRequiredService<IReceptorRegistry>(), scope.ServiceProvider);
    var tick = new SagaCompletionWatchdogTickEvent { StreamId = Guid.NewGuid(), SagaName = sagaName, EntityId = Guid.NewGuid() };
    await invoker.InvokeAsync(new MessageEnvelope<SagaCompletionWatchdogTickEvent> {
      MessageId = MessageId.New(),
      Payload = tick,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    }, stage);
  }

  [Test]
  public async Task HandWrittenSagaTick_DeliveredAtTheInboxStage_ReachesTheSagaAsync() {
    var handWritten = new RecordingParticipant(HAND_WRITTEN);
    await using var provider = _host(handWritten);
    await _startTheRouterRegistrarAsync(provider);

    await _deliverAsync(provider, HAND_WRITTEN, LifecycleStage.PostInboxInline);

    await Assert.That(handWritten.Received).IsEqualTo(1)
      .Because("the tick has to reach the saga that armed it, exactly once, alongside the generated receivers");
  }

  [Test]
  public async Task WithoutTheRouter_AHandWrittenSagaTick_ReachesNothingAsync() {
    var handWritten = new RecordingParticipant(HAND_WRITTEN);
    await using var provider = _host(handWritten);
    // Registrar deliberately not started: the host as it was before the framework owned a receiver.

    await _deliverAsync(provider, HAND_WRITTEN, LifecycleStage.PostInboxInline);

    await Assert.That(handWritten.Received).IsEqualTo(0)
      .Because("this is the defect — delivered on time, received by nothing; the test above only means something if this one holds");
  }

  [Test]
  public async Task HandWrittenSagaTick_AtTheSendingStage_DoesNotReachTheSagaAsync() {
    var handWritten = new RecordingParticipant(HAND_WRITTEN);
    await using var provider = _host(handWritten);
    await _startTheRouterRegistrarAsync(provider);

    await _deliverAsync(provider, HAND_WRITTEN, LifecycleStage.PreOutboxInline);

    await Assert.That(handWritten.Received).IsEqualTo(0)
      .Because("a tick is armed for later; reaching the saga at arming time would re-arm at once and cascade");
  }

  [Test]
  public async Task SagaAttributeTick_IsLeftToItsGeneratedReceiverAsync() {
    var handWritten = new RecordingParticipant(HAND_WRITTEN);
    await using var provider = _host(handWritten);
    await _startTheRouterRegistrarAsync(provider);

    await _deliverAsync(provider, GeneratorTestDefaultSaga.SagaName, LifecycleStage.PostInboxInline);

    await Assert.That(handWritten.Received).IsEqualTo(0)
      .Because("routing is by saga name, and a [Saga]-declared saga is not a router participant");
  }

  private sealed class RecordingParticipant(string sagaName) : ISagaWatchdogParticipant {
    private int _received;
    public int Received => Volatile.Read(ref _received);
    public string SagaName => sagaName;

    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(
        SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _received);
      return Task.FromResult(WatchdogTickOutcome.ReArmed);
    }
  }

  private sealed class NoOpEmitter : ISagaEventEmitter {
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent => Task.CompletedTask;
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken)
      where TEvent : IEvent => Task.FromResult(true);
  }
}
