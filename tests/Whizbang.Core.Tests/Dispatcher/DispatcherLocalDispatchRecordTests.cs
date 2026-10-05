// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A publish that runs its receptors on the local path records that on the envelope it stores, so the
/// receptor dedup store can see the firing at every stage the message reaches afterward.
/// </summary>
/// <remarks>
/// <para>
/// The dedup store keeps "a receptor that fired for a message does not fire again for it" by reading
/// records on the envelope. The local path of a publish invokes its receptors directly, not through
/// the receptor invoker, so it wrote no record, and the envelope it stored for the outbox carried
/// none. A later stage that reached the same receptor for the same message, in a host where no
/// same-service rule applied, therefore fired it a second time: a saga's watchdog tick, published
/// without a schedule by the stranded-saga sweep, was handled once by the sweep's host and again by
/// another host of the same saga.
/// </para>
/// <para>
/// The record is written before the envelope is stored, because the outbox write runs beside the local
/// path rather than after it.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[NotInParallel("LocalDispatchRecord")]
public class DispatcherLocalDispatchRecordTests {

  public record LocalDispatchRecordProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>An event no receptor handles.</summary>
  public record LocalDispatchUnhandledProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>A receptor at the default stages, as a receptor with no [FireAt] is.</summary>
  public class LocalDispatchRecordProbeReceptor : IReceptor<LocalDispatchRecordProbeEvent> {
    public ValueTask HandleAsync(LocalDispatchRecordProbeEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private const string RECEPTOR_ID_SUFFIX = nameof(LocalDispatchRecordProbeReceptor);

  /// <summary>An event two services both handle with the same receptor class.</summary>
  public record SharedHandlerProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>An event two services both handle with a receptor that must run once overall.</summary>
  public record OnceOverallProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>How many times each probe stream was handled, across every service in the test.</summary>
  private static readonly ConcurrentDictionary<Guid, int> _handlings = new();

  /// <summary>Shared handler code that writes to the store of whichever service runs it.</summary>
  public class SharedHandlerProbeReceptor : IReceptor<SharedHandlerProbeEvent> {
    public ValueTask HandleAsync(SharedHandlerProbeEvent message, CancellationToken cancellationToken = default) {
      _handlings.AddOrUpdate(message.StreamId, 1, (_, n) => n + 1);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>A handler with an external effect, such as sending an email, that has to happen once.</summary>
  [ReceptorOnceAcrossServices]
  public class OnceOverallProbeReceptor : IReceptor<OnceOverallProbeEvent> {
    public ValueTask HandleAsync(OnceOverallProbeEvent message, CancellationToken cancellationToken = default) {
      _handlings.AddOrUpdate(message.StreamId, 1, (_, n) => n + 1);
      return ValueTask.CompletedTask;
    }
  }

  private static readonly LifecycleStage[] _stagesOfTheInbox = [
    LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
    LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
  ];

  /// <summary>
  /// A receptor class registered in two services runs in both: once on the publishing service's local
  /// path, and once when the other service receives the message, though the envelope it receives
  /// records the first firing.
  /// </summary>
  [Test]
  public async Task SharedReceptor_InTwoServices_RunsInBothAsync() {
    var evt = new SharedHandlerProbeEvent(Guid.CreateVersion7());

    var handled = await _publishInServiceAThenReceiveInServiceBAsync(evt, evt.StreamId);

    await Assert.That(handled).IsEqualTo(2)
      .Because("each service writes to its own store, so a record from the other service must not stop it");
  }

  /// <summary>
  /// A receptor marked <c>[ReceptorOnceAcrossServices]</c> runs once: the other service sees the
  /// publishing service's record and skips it.
  /// </summary>
  [Test]
  public async Task OnceAcrossServicesReceptor_InTwoServices_RunsOnceAsync() {
    var evt = new OnceOverallProbeEvent(Guid.CreateVersion7());

    var handled = await _publishInServiceAThenReceiveInServiceBAsync(evt, evt.StreamId);

    await Assert.That(handled).IsEqualTo(1)
      .Because("an external effect must happen once for the message, not once per service");
  }

  /// <summary>
  /// Publishes in one service, then runs the inbox stages over the stored envelope in a second service
  /// built from the same receptors, and returns how many times the event's stream was handled.
  /// </summary>
  private static async Task<int> _publishInServiceAThenReceiveInServiceBAsync<TEvent>(TEvent evt, Guid streamId) where TEvent : IEvent {
    var (serviceA, stored) = _dispatcher(services =>
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "service-a", "host-a", 1)));
    await serviceA.PublishAsync(evt);
    var handledAtPublish = _handlings.GetValueOrDefault(streamId);

    var serviceB = new ServiceCollection();
    serviceB.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "service-b", "host-b", 2));
    serviceB.AddReceptors();
    serviceB.AddWhizbangDispatcher();
    await using var providerB = serviceB.BuildServiceProvider();
    await using var scope = providerB.CreateAsyncScope();
    var invoker = new ReceptorInvoker(providerB.GetRequiredService<IReceptorRegistry>(), scope.ServiceProvider);
    var received = new MessageEnvelope<TEvent> {
      MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
      Payload = evt,
      Hops = [],
      ReceptorInvocations = [.. stored.Single()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    foreach (var stage in _stagesOfTheInbox) {
      await invoker.InvokeAsync(received, stage);
    }

    await Assert.That(handledAtPublish).IsEqualTo(1)
      .Because("the publishing service runs the receptor on its local path");
    return _handlings.GetValueOrDefault(streamId);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task PublishAsync_EventWithADefaultStageReceptor_StoredEnvelopeRecordsTheLocalFiringAsync(bool withOptions) {
    var (dispatcher, stored) = _dispatcher();
    var evt = new LocalDispatchRecordProbeEvent(Guid.CreateVersion7());

    if (withOptions) {
      await dispatcher.PublishAsync(evt, new DispatchOptions());
    } else {
      await dispatcher.PublishAsync(evt);
    }

    var record = stored.Single().Single(r => r.ReceptorId.EndsWith(RECEPTOR_ID_SUFFIX, StringComparison.Ordinal));
    await Assert.That(record.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline)
      .Because("the local path ran the receptor inline, in the publishing call");
    await Assert.That(record.ServiceName).IsNotEqualTo(string.Empty)
      .Because("the record names the service whose local path fired, as the invoker's records do");
  }

  /// <summary>A publish scheduled for later runs nothing locally, so it records nothing.</summary>
  [Test]
  public async Task PublishAsync_ScheduledForLater_RecordsNothingAsync() {
    var (dispatcher, stored) = _dispatcher();

    await dispatcher.PublishAsync(new LocalDispatchRecordProbeEvent(Guid.CreateVersion7()),
      new DispatchOptions().WithScheduledFor(DateTimeOffset.UtcNow.AddMinutes(5)));

    await Assert.That(stored.Single()).IsEmpty()
      .Because("recording a firing that has not happened would stop the receptor at the stage that does run it");
  }

  [Test]
  public async Task PublishAsync_EventWithNoReceptor_RecordsNothingAsync() {
    var (dispatcher, stored) = _dispatcher();

    await dispatcher.PublishAsync(new LocalDispatchUnhandledProbeEvent(Guid.CreateVersion7()));

    await Assert.That(stored.Single()).IsEmpty();
  }

  /// <summary>With tracking off, no dedup store, or no registry, nothing is recorded, as the invoker records nothing then.</summary>
  /// <param name="without">What the host lacks.</param>
  [Test]
  [Arguments("tracking")]
  [Arguments("store")]
  [Arguments("registry")]
  public async Task PublishAsync_NothingToRecordWith_RecordsNothingAsync(string without) {
    var (dispatcher, stored) = _dispatcher(services => {
      switch (without) {
        case "tracking":
          services.Configure<WhizbangOptions>(o => o.Guardrails.ReceptorInvocationTracking = ReceptorInvocationTracking.Off);
          break;
        case "store":
          services.RemoveAll<IReceptorDedupStore>();
          break;
        default:
          services.RemoveAll<IReceptorRegistry>();
          break;
      }
    });

    await dispatcher.PublishAsync(new LocalDispatchRecordProbeEvent(Guid.CreateVersion7()));

    await Assert.That(stored.Single()).IsEmpty();
  }

  private static (IDispatcher Dispatcher, List<List<ReceptorInvocationRecord>> Stored) _dispatcher(Action<IServiceCollection>? configure = null) {
    var serializer = new SnapshotSerializer();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer>(serializer);
    services.AddScoped<IWorkCoordinatorStrategy, DiscardingStrategy>();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    configure?.Invoke(services);
    return (services.BuildServiceProvider().GetRequiredService<IDispatcher>(), serializer.Stored);
  }

  /// <summary>Keeps what each envelope carried at the moment it was serialized for the outbox.</summary>
  private sealed class SnapshotSerializer : IEnvelopeSerializer {
    public List<List<ReceptorInvocationRecord>> Stored { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Stored.Add(envelope.ReceptorInvocations is null ? [] : [.. envelope.ReceptorInvocations]);
      var json = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = envelope.DispatchContext,
      };
      return new SerializedEnvelope(json,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class DiscardingStrategy : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}
