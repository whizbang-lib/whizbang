using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The NON-DURABLE RECEIVE PATH for the control class (topology arc phase 9): control messages are
/// <b>receive → compare → discard</b>. No inbox row, no completion bookkeeping, no dead-lettering.
/// <para>
/// The reasoning the phase inherits from the dead-letter boundary and extends to the receive
/// boundary: a supersedable control signal is re-derived on the next cadence, so a stored copy is
/// already worthless when anyone reads it — and it is not inert. The durable inbox feeds retry and
/// recovery, so a burst of control failures becomes a backlog that every subsequent boot replays.
/// Observed live before this rule existed at the DLQ boundary: tens of thousands of control-plane
/// rows per service, surviving repeated queue purges.
/// </para>
/// <para>
/// "Compare" is not skipped — it MOVES. The class's receptors run inline at the receive boundary,
/// at the SAME lifecycle stage the durable path would have fired them at, so a control receptor is
/// unchanged; only the durability disappears. A failing comparison drops the message and lets the
/// next cadence's copy try again, because dead-lettering it would resurrect exactly the backlog
/// this path removes.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerControlClassReceiveTests {
  private const string CONTROL_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core]], Whizbang.Core";
  private const string DOMAIN_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Consumed, TestApp]], Whizbang.Core";

  [Test]
  public async Task ControlMessage_NonDurableReceive_WritesNoInboxRowAsync() {
    var coordinator = new CountingWorkCoordinator();
    var (worker, transport, sp) = _buildWorker(coordinator, nonDurableReceive: true);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONTROL_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(coordinator.StoreCallCount).IsEqualTo(0)
        .Because("no inbox row — the whole point of the class's receive path");
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0);
    }
  }

  [Test]
  public async Task ControlMessage_NonDurableReceive_StillComparesAsync() {
    // Discard without compare would silently disable stream integrity — the receptors ARE the
    // comparison. The stage is the one the durable path fires, so consumers are unchanged.
    var coordinator = new CountingWorkCoordinator();
    var invoker = new RecordingReceptorInvoker();
    var (worker, transport, sp) = _buildWorker(coordinator, nonDurableReceive: true, invoker: invoker);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONTROL_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(invoker.Stages).Contains(LifecycleStage.PostInboxInline);
    }
  }

  [Test]
  public async Task ControlMessage_ComparisonThrows_DropsWithoutDeadLetteringAsync() {
    // A failing control comparison must not dead-letter and must not rethrow: rethrowing abandons
    // the broker message, which is redelivery — the loop this class exists to make impossible.
    var coordinator = new CountingWorkCoordinator();
    var deadLetters = new RecordingDeadLetterStore();
    var invoker = new RecordingReceptorInvoker { Throw = true };
    var (worker, transport, sp) = _buildWorker(
      coordinator, nonDurableReceive: true, invoker: invoker, deadLetters: deadLetters);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONTROL_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(deadLetters.Moved).IsEmpty()
        .Because("control-plane failures DROP rather than dead-letter — the receive-boundary "
               + "extension of the rule the dead-letter boundary already applies");
      await Assert.That(coordinator.StoreCallCount).IsEqualTo(0);
    }
  }

  [Test]
  public async Task DomainMessage_InTheSameBatch_StillTakesTheDurablePathAsync() {
    // The gate must be per MESSAGE, not per batch: a mixed batch is normal on a shared entity.
    var coordinator = new CountingWorkCoordinator();
    var (worker, transport, sp) = _buildWorker(coordinator, nonDurableReceive: true);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([
        new TransportMessage(_envelope(), CONTROL_ENVELOPE_TYPE),
        new TransportMessage(_envelope(), DOMAIN_ENVELOPE_TYPE),
      ]);
      await cts.CancelAsync();

      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
        .Because("exactly the domain message is stored — the control message never reaches the store");
    }
  }

  [Test]
  public async Task ControlMessage_NonDurableReceiveDisabled_TakesTheDurablePathAsync() {
    // The opt-in guarantee: with the migration step off, a control message is stored exactly as
    // it is today — byte-identical pre-phase-9 behavior.
    var coordinator = new CountingWorkCoordinator();
    var (worker, transport, sp) = _buildWorker(coordinator, nonDurableReceive: false);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONTROL_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1);
    }
  }

  [Test]
  public async Task DurableSystemCommand_IsNeverTakenOffTheInboxAsync() {
    // The split, enforced at the receive boundary too: a durable system command carries the
    // control-plane MARKER but not the control-class TAG, and losing one loses operator intent.
    var coordinator = new CountingWorkCoordinator();
    var (worker, transport, sp) = _buildWorker(coordinator, nonDurableReceive: true);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(),
        "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Commands.System.RebuildPerspectiveCommand, Whizbang.Core]], Whizbang.Core")]);
      await cts.CancelAsync();

      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
        .Because("RebuildPerspectiveCommand is IControlPlaneMessage but NOT control class — the "
               + "marker is about security and dead-lettering, not about expiring value");
    }
  }

  [Test]
  public async Task ControlClassResolver_RecognizesTaggedTypesByNameAsync() {
    // The receive boundary holds a type-NAME string, never a Type, so membership must be
    // name-keyed — the TransportNamespaceResolver idiom.
    var resolver = _resolver();

    await Assert.That(resolver.IsControlClass(TypeNameFormatter.Format(typeof(IntegrityCheckpoint)))).IsTrue();
    await Assert.That(resolver.IsControlClass(
      TypeNameFormatter.Format(typeof(Whizbang.Core.Commands.System.RebuildPerspectiveCommand)))).IsFalse();
    await Assert.That(resolver.IsControlClass("Nothing.Anyone.Registered, Nowhere")).IsFalse()
      .Because("an unresolvable name must fail SAFE — keeping the durable path, never dropping");
    await Assert.That(resolver.IsControlClass("")).IsFalse();
  }

  #region Fixtures

  /// <summary>
  /// Resolver over an EXPLICIT registration set rather than the process-global
  /// <see cref="MessageTagRegistry"/>: another suite legitimately clears that registry to test
  /// registration itself, and a receive-path lock must not depend on winning that race.
  /// </summary>
  private static ControlClassResolver _resolver() => new(() => [
    new MessageTagRegistration {
      MessageType = typeof(IntegrityCheckpoint),
      AttributeType = typeof(SystemControlTagAttribute),
      Tag = SystemTags.CONTROL,
      PayloadBuilder = _ => default,
      AttributeFactory = () => new SystemControlTagAttribute { Tag = SystemTags.CONTROL },
    },
  ]);

  private static MessageEnvelope<JsonElement> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static (TransportConsumerWorker Worker, ControlReceiveTransport Transport, ServiceProvider Sp)
      _buildWorker(
      CountingWorkCoordinator coordinator,
      bool nonDurableReceive,
      RecordingReceptorInvoker? invoker = null,
      RecordingDeadLetterStore? deadLetters = null) {
    var transport = new ControlReceiveTransport();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    if (deadLetters is not null) {
      services.AddScoped<IDeadLetterStore>(_ => deadLetters);
    }
    if (invoker is not null) {
      services.AddScoped<IReceptorInvoker>(_ => invoker);
    }
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: new AlwaysConsumedRegistry(),
      controlClass: Options.Create(new ControlClassOptions { NonDurableReceive = nonDurableReceive }),
      controlClassResolver: _resolver(),
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    return (worker, transport, sp);
  }

  private sealed class AlwaysConsumedRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private sealed class CountingWorkCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int StoreCallCount { get; private set; }
    public new int StoredInboxCount { get; private set; }

    Task IWorkCoordinator.StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken) {
      StoreCallCount++;
      StoredInboxCount += messages.Length;
      return Task.CompletedTask;
    }
  }

  private sealed class RecordingReceptorInvoker : IReceptorInvoker {
    public List<LifecycleStage> Stages { get; } = [];
    public bool Throw { get; init; }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Stages.Add(stage);
      return Throw
        ? ValueTask.FromException(new InvalidOperationException("comparison failed"))
        : ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingDeadLetterStore : IDeadLetterStore {
    public List<Guid> Moved { get; } = [];

    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moved.Add(sourceId);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class ControlReceiveTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public TaskCompletionSource SubscribedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      SubscribedSignal.TrySetResult();
      return Task.FromResult<ISubscription>(new NopSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotImplementedException();

    public Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) =>
      _batchHandler is null
        ? throw new InvalidOperationException("SubscribeBatchAsync was never called by the worker.")
        : _batchHandler(batch, CancellationToken.None);

    private sealed class NopSubscription : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() { IsActive = false; }
    }
  }

  #endregion
}
