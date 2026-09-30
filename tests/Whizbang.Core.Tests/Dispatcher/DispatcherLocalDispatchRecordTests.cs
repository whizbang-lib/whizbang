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
