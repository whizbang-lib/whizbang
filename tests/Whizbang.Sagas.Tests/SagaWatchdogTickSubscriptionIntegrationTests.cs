using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Sagas.Services;
using Whizbang.Sagas.Tests.Generators;
using Whizbang.Testing.MultiService;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// A host whose sagas receive watchdog ticks only through the framework's router subscribes to the
/// tick's topic, so a tick published over a transport reaches the saga that armed it.
/// </summary>
/// <remarks>
/// <para>
/// The router is registered at startup, and transport subscriptions used to be derived only from the
/// receptors and perspectives the source generator finds. A host that registered its hand-written
/// saga with <c>AddSagaService</c> and had no tick receptor of its own therefore armed ticks, published
/// them, and never subscribed to the topic they were published to: they sat unread on the broker, the
/// watchdog never fired, and the stranded-saga sweep re-armed ticks that met the same end. The
/// in-process delivery tests could not see it, because they hand the tick straight to the invoker.
/// </para>
/// <para>
/// These tests drive a real transport consumer over the multi-service harness's in-memory wire, whose
/// boundary is serialized bytes and whose delivery is by topic. The compile-time namespace registry is
/// replaced with a fixed one, so each host subscribes from exactly the compile-time consumers it is
/// meant to model rather than from whatever the test assembly's own generated receptors discover.
/// A tick the consumer stores is then handed to the host's invoker at the stage the inbox dispatches
/// it, where the router hands it to the saga.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/SagaServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Routing/RuntimeEventSubscription.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Routing/EventSubscriptionDiscovery.cs</code-under-test>
[Category("Integration")]
[Category("Saga")]
[NotInParallel("MultiServiceHarness")]
public class SagaWatchdogTickSubscriptionIntegrationTests {
  private const string SERVICE = "saga-host";
  private const string SAGA = "HandWrittenImport";
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(10);

  /// <summary>What the compile-time registry of a host with its own tick receptor contains.</summary>
  private static readonly string[] _tickReceptorNamespaces = [NamespaceRoutingStrategy.DefaultTypeToTopic(typeof(SagaCompletionWatchdogTickEvent))];

  [Test]
  public async Task AddSagaServiceOnly_SubscribesToTheTicksTopic_AndAPublishedTickReachesTheSagaAsync() {
    var ledger = new TickLedger();
    await using var harness = await _startAsync(ledger, compileTimeNamespaces: [], withSagas: true);
    var service = harness.GetService(SERVICE);
    await _startTheRouterAsync(service);
    var topic = _tickTopic(service);

    await _publishTickAsync(harness, topic);

    var stored = await service.Inbox.WaitForInboxAsync(1, _signalTimeout);
    await _dispatchAtTheInboxStageAsync(service, stored.Single());

    await Assert.That(_subscribedAddresses(service)).Contains(topic)
      .Because("the tick is published to its namespace topic; a host that consumes it must subscribe there");
    await Assert.That(ledger.Recovered).IsEqualTo(1)
      .Because("AddSagaService alone must be enough for the saga to receive its watchdog over a transport");
  }

  /// <summary>
  /// A tick the stranded-saga sweep arms is published by the saga's own service and received by the
  /// same service. That is the normal topology for a hand-written saga, and the tick must get through
  /// every step a received message takes: the receive gate stores it, the inbox gate keeps it, and the
  /// inbox dispatch hands it to the saga.
  /// </summary>
  /// <remarks>
  /// The router used to be registered at the one inbox stage the receptor invoker skips for a message
  /// whose last hop is this same service, on the grounds that such a message already ran its
  /// receptors when it was published. The router runs at no publishing stage, so a tick armed and
  /// received by one service was stored, committed and handed to nobody, with nothing logged above
  /// Debug.
  /// </remarks>
  [Test]
  public async Task SweepTickPublishedByTheSagasOwnService_IsReceivedKeptAndReachesTheSagaAsync() {
    var ledger = new TickLedger();
    await using var harness = await _startAsync(ledger, compileTimeNamespaces: [], withSagas: true);
    var service = harness.GetService(SERVICE);
    await _startTheRouterAsync(service);
    var topic = _tickTopic(service);
    var self = service.Provider.GetRequiredService<IServiceInstanceProvider>().ToInfo();

    await _publishTickAsync(harness, topic, publishedBy: self, sweepShaped: true);

    var row = (await service.Inbox.WaitForInboxAsync(1, _signalTimeout)).Single();
    var inboxGate = service.Provider.GetRequiredService<IMessageDiscardPolicy>().EvaluateInbox(row.MessageType);
    await Assert.That(inboxGate.ShouldDiscard).IsFalse()
      .Because($"the inbox gate must find the runtime-registered router for '{row.MessageType}'");
    await _dispatchAtTheInboxStageAsync(service, row);

    await Assert.That(ledger.Recovered).IsEqualTo(1)
      .Because("a tick its own service published and received has to reach the saga, or the sweep recovers nothing");
  }

  /// <summary>
  /// The defect, reproduced: without the router's declaration nothing on this host subscribes to the
  /// tick's topic, so a published tick is never received. The test above means something only if
  /// this one holds. Delivery on the harness wire completes inside the publish call, so nothing
  /// stored once it returns is nothing received.
  /// </summary>
  [Test]
  public async Task WithoutWhizbangSagas_TheTicksTopicIsNotSubscribed_AndAPublishedTickIsNeverReceivedAsync() {
    var ledger = new TickLedger();
    await using var harness = await _startAsync(ledger, compileTimeNamespaces: [], withSagas: false);
    var service = harness.GetService(SERVICE);
    var topic = _tickTopic(service);

    await _publishTickAsync(harness, topic);

    await Assert.That(_subscribedAddresses(service)).DoesNotContain(topic);
    await Assert.That(service.Inbox.StoredInboxMessages).IsEmpty()
      .Because("with no subscription on the tick's topic the tick is published and received by nothing");
  }

  /// <summary>
  /// A host that still has its own receptor for the tick already subscribes to the topic through
  /// compile-time discovery. The router's declaration names the same topic, and the host must still
  /// subscribe once: a second subscription is a second delivery, and a second recovery re-arms the
  /// watchdog twice. This models the pattern from before <c>AddSagaService</c>: the host's receptor
  /// forwards the tick to its saga, and the saga is not a router participant.
  /// </summary>
  [Test]
  public async Task HostWithItsOwnTickReceptor_SubscribesOnce_AndEachTickIsRecoveredOnceAsync() {
    var ledger = new TickLedger();
    var handWrittenSaga = new RecordingSaga(ledger);
    await using var harness = await _startAsync(ledger, compileTimeNamespaces: _tickReceptorNamespaces, withSagas: true, registerParticipant: false);
    var service = harness.GetService(SERVICE);
    await _startTheRouterAsync(service);
    // The host's own receptor, registered as the generated registry would dispatch it: at the inbox stage.
    var ownReceptor = new DelegateReceptor<SagaCompletionWatchdogTickEvent>(tick =>
      new ValueTask(handWrittenSaga.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None)));
    service.Provider.GetRequiredService<IReceptorRegistry>().Register(ownReceptor, LifecycleStage.PostInboxInline);
    var topic = _tickTopic(service);

    await _publishTickAsync(harness, topic);

    var stored = service.Inbox.StoredInboxMessages;
    await Assert.That(stored.Count).IsEqualTo(1)
      .Because("one subscription per topic: the router's declaration must not add a second one beside the receptor's");
    await _dispatchAtTheInboxStageAsync(service, stored.Single());

    await Assert.That(_subscribedAddresses(service).Count(a => a == topic)).IsEqualTo(1);
    await Assert.That(ownReceptor.Received).IsEqualTo(1);
    await Assert.That(ledger.Recovered).IsEqualTo(1)
      .Because("each tick is recovered once; a second recovery would re-arm the watchdog twice");
  }

  /// <summary>
  /// The tick's subscription is made and reported by the same code path as every other one, so an
  /// operator reading startup logs sees it named: its absence is what made the defect diagnosable.
  /// </summary>
  [Test]
  public async Task AddSagaServiceOnly_TheTickSubscription_IsLoggedAndHealthyLikeAnyOtherAsync() {
    var logger = new CapturingLogger<TransportConsumerWorker>();
    await using var harness = await _startAsync(new TickLedger(), compileTimeNamespaces: [], withSagas: true, workerLogger: logger);
    var service = harness.GetService(SERVICE);
    var topic = _tickTopic(service);

    var subscribed = logger.Messages.Where(m => m.StartsWith("✓ Subscribed to ", StringComparison.Ordinal)).ToList();
    await Assert.That(subscribed).Contains($"✓ Subscribed to {topic} (routing key: #)")
      .Because("the tick's subscription is announced exactly as the host's other event subscriptions are");
    await Assert.That(subscribed.Count).IsGreaterThan(1)
      .Because("it sits among the host's other subscriptions rather than in a path of its own");
    var state = service.Worker.SubscriptionStates.Single(s => s.Key.Address == topic).Value;
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
  }

  private static async Task<MultiServiceHarness> _startAsync(
      TickLedger ledger,
      string[] compileTimeNamespaces,
      bool withSagas,
      bool registerParticipant = true,
      CapturingLogger<TransportConsumerWorker>? workerLogger = null) {
    // The saga assembly's JSON registrations run on first use of one of its types; the wire's options
    // are built when the harness starts, so make sure the tick is known to them first.
    _ = new SagaCompletionWatchdogTickEvent();

    return await MultiServiceHarness.Create()
      .AddService(SERVICE, svc => svc
        // The framework's default topology, as a host that configures nothing gets it.
        .WithRouting(static _ => { })
        .Configure(services => {
          services.AddSingleton<IEventNamespaceRegistry>(new FixedNamespaceRegistry(compileTimeNamespaces));
          // Gate the receive path on the host's real consumers rather than the harness's claim-all default.
          services.AddSingleton<IReceptorRegistryQuery>(sp =>
            new WhizbangReceptorRegistryQueryAdapter(sp.GetRequiredService<IReceptorRegistry>()));
          if (workerLogger is not null) {
            services.AddSingleton<ILogger<TransportConsumerWorker>>(workerLogger);
          }
          // The test assembly's [Saga]-declared sagas and their generated tick receivers share the
          // host, as they can in a real one; routing by saga name keeps each tick to its own saga.
          services.AddSingleton<ISagaEventEmitter, NoOpEmitter>();
          services.AddGeneratorTestDefaultSaga();
          services.AddGeneratorTestCustomBaseSaga();
          services.AddGeneratorTestChainedSaga();
          global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddReceptors(services);
          global::Whizbang.Sagas.Tests.Generated.DispatcherRegistrations.AddWhizbangReceptorRegistry(services);
          services.AddSingleton(ledger);
          if (withSagas) {
            services.AddWhizbangSagas();
            if (registerParticipant) {
              services.AddSagaService<RecordingSaga>();
            }
          }
        }))
      .StartAsync();
  }

  /// <summary>
  /// Starts the registrar the host would start with its other hosted services. The harness starts
  /// only the consumer worker, so the registrar is built from the host's container and started here.
  /// </summary>
  private static Task _startTheRouterAsync(MultiServiceHarness.ServiceRuntime service)
    => ActivatorUtilities.CreateInstance<SagaWatchdogTickRouterRegistrar>(service.Provider).StartAsync(CancellationToken.None);

  /// <summary>The topic the host's own outbox strategy publishes a tick to.</summary>
  private static string _tickTopic(MultiServiceHarness.ServiceRuntime service) {
    var routing = service.Provider.GetRequiredService<IOptions<RoutingOptions>>().Value;
    return service.Provider.GetRequiredService<IOutboxRoutingStrategy>().GetDestination(
      typeof(SagaCompletionWatchdogTickEvent),
      routing.OwnedDomains,
      MessageKindDetector.Detect(typeof(SagaCompletionWatchdogTickEvent))).Address;
  }

  private static List<string> _subscribedAddresses(MultiServiceHarness.ServiceRuntime service)
    => [.. service.Worker.SubscriptionStates.Keys.Select(d => d.Address)];

  /// <summary>
  /// Publishes a tick onto the wire as the saga's service does: as the system, in the saga's tenant,
  /// which is the scope the inbox later establishes before any receptor runs.
  /// </summary>
  /// <param name="harness">The running harness.</param>
  /// <param name="topic">The tick's topic.</param>
  /// <param name="publishedBy">The service the tick's hop names; an unknown one when omitted.</param>
  /// <param name="sweepShaped">Shapes the tick as the stranded-saga sweep arms it: already at the stall limit.</param>
  private static Task _publishTickAsync(
      MultiServiceHarness harness, string topic, ServiceInstanceInfo? publishedBy = null, bool sweepShaped = false) {
    var tick = new SagaCompletionWatchdogTickEvent { StreamId = Guid.CreateVersion7(), SagaName = SAGA, EntityId = Guid.CreateVersion7() };
    if (sweepShaped) {
      tick.LastObservedAt = DateTimeOffset.UtcNow;
      tick.ConsecutiveStallCount = new SagaOptions().MaxConsecutiveStalls - 1;
    }
    var envelope = new MessageEnvelope<SagaCompletionWatchdogTickEvent> {
      MessageId = MessageId.New(),
      Payload = tick,
      Hops = [new MessageHop {
        ServiceInstance = publishedBy ?? ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        Scope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = "tenant-a", UserId = "SYSTEM" }),
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var envelopeType =
      $"Whizbang.Core.Messaging.MessageEnvelope`1[[{TypeNameFormatter.AssemblyQualifiedName(typeof(SagaCompletionWatchdogTickEvent))}]], Whizbang.Core";
    return harness.Wire.PublishAsync(envelope, new Whizbang.Core.Transports.TransportDestination(topic), envelopeType);
  }

  /// <summary>
  /// Hands a stored inbox row to the host's invoker as the inbox dispatcher does: the payload
  /// deserialized by name, the envelope rebuilt around it, invoked at each inline inbox stage in the
  /// dispatcher's order, before and after the row is committed.
  /// </summary>
  private static async Task _dispatchAtTheInboxStageAsync(MultiServiceHarness.ServiceRuntime service, InboxMessage row) {
    var payload = service.Provider.GetRequiredService<ILifecycleMessageDeserializer>()
      .DeserializeFromJsonElement(row.Envelope.Payload, row.MessageType);
    var envelope = row.Envelope.ReconstructWithPayload(payload);
    foreach (var stage in (LifecycleStage[])[LifecycleStage.PreInboxInline, LifecycleStage.PostInboxInline]) {
      await using var scope = service.Provider.CreateAsyncScope();
      var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
      await invoker.InvokeAsync(envelope, stage, new LifecycleExecutionContext {
        CurrentStage = stage,
        MessageSource = MessageSource.Inbox,
        AttemptNumber = 1,
      });
    }
  }

  /// <summary>Counts recoveries across every saga instance the host builds.</summary>
  private sealed class TickLedger {
    private int _recovered;
    public int Recovered => Volatile.Read(ref _recovered);
    public void Record() => Interlocked.Increment(ref _recovered);
  }

  /// <summary>A hand-written saga, as <c>AddSagaService</c> exposes one to the router.</summary>
  private sealed class RecordingSaga(TickLedger ledger) : ISagaWatchdogParticipant {
    public string SagaName => SAGA;

    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(
        SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken) {
      ledger.Record();
      return Task.FromResult(WatchdogTickOutcome.ReArmed);
    }
  }

  /// <summary>
  /// A receptor registered at runtime. Generic so the source generator, which skips open generic
  /// types, does not also discover it as a compile-time receptor of every host in this assembly.
  /// </summary>
  private sealed class DelegateReceptor<TMessage>(Func<TMessage, ValueTask> handle) : IReceptor<TMessage> where TMessage : IMessage {
    private int _received;
    public int Received => Volatile.Read(ref _received);

    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _received);
      return handle(message);
    }
  }

  private sealed class FixedNamespaceRegistry(string[] namespaces) : IEventNamespaceRegistry {
    private readonly HashSet<string> _namespaces = new(namespaces, StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> GetPerspectiveEventNamespaces() => new HashSet<string>();
    public IReadOnlySet<string> GetReceptorEventNamespaces() => _namespaces;
    public IReadOnlySet<string> GetAllEventNamespaces() => _namespaces;
  }

  private sealed class CapturingLogger<T> : ILogger<T> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_lock) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _messages.Add(formatter(state, exception));
      }
    }
  }

  private sealed class NoOpEmitter : ISagaEventEmitter {
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent => Task.CompletedTask;
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken)
      where TEvent : IEvent => Task.FromResult(true);
  }
}
