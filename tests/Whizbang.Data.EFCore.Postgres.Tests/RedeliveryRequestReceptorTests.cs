using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests (no Postgres) for the stream-integrity R1b bridge: <see cref="RedeliveryRequestReceptor"/>
/// turns a received <see cref="RequestRedeliveryCommand"/> into a coordinator selection pumped back to
/// the wire as composites targeted at the requester, with the requester's MaxEvents clamped by the
/// origin's <see cref="RedeliveryPumpOptions.MaxEventsPerRequest"/>; and
/// <see cref="RedeliveryRequestReceptorRegistrar"/> runtime-registers it at the three default stages.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptorRegistrar.cs</code-under-test>
public class RedeliveryRequestReceptorTests {

  [Test]
  public async Task Receptor_MapsSelectionAndPublishesTargetedCompositesAsync() {
    var coordinator = new _selectingCoordinator();
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var streamId = TrackedGuid.NewMedo().Value;
    var e1 = TrackedGuid.NewMedo().Value;
    var e2 = TrackedGuid.NewMedo().Value;
    coordinator.Selection = [_evt(streamId, e1, 1), _evt(streamId, e2, 2)];
    await using var sp = _buildProvider(coordinator, transport, serializer);
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    var typeFilter = new List<string> { "Contracts.ProbeHappened" };
    var streamFilter = new List<Guid> { streamId };
    await receptor.HandleAsync(new RequestRedeliveryCommand {
      TenantScope = "tenant-a",
      EventTypes = typeFilter,
      StreamIds = streamFilter,
      FromCommitSequence = 10,
      ToCommitSequence = 99,
      MaxEvents = 5,
      RequesterService = "damaged-svc",
      Topic = "events-topic",
      StateOnly = true
    });

    var request = coordinator.LastRequest!;
    await Assert.That(request.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(request.EventTypes!).IsEquivalentTo(typeFilter);
    await Assert.That(request.StreamIds!).IsEquivalentTo(streamFilter);
    await Assert.That(request.FromCommitSequence).IsEqualTo(10L);
    await Assert.That(request.ToCommitSequence).IsEqualTo(99L);
    await Assert.That(request.MaxEvents).IsEqualTo(5)
      .Because("a requested cap below the origin's cap passes through unclamped.");

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var (envelope, destination, _) = transport.Published[0];
    await Assert.That(destination.Address).IsEqualTo("events-topic");
    await Assert.That(envelope.Target).IsEqualTo("damaged-svc")
      .Because("re-delivery bundles are DIRECTED at the requester — every other consumer discards them.");
    var composite = serializer.Captured[0].Payload;
    await Assert.That(composite.InnerEventIds).IsEquivalentTo([e1, e2])
      .Because("original event ids ride the bundle — identity is what makes convergence idempotent.");
    await Assert.That(composite.OriginServiceId).IsEqualTo(coordinator.LocalServiceId)
      .Because("the receptor names THIS origin on the bundle so repaired children recount under " +
               "the origin identity Phase B accounting keys on.");
    await Assert.That(composite.InnerCommitSequences!).IsEquivalentTo([(long?)1, 2])
      .Because("each child's ORIGINAL commit sequence rides the bundle.");
    await Assert.That(transport.Published[0].Envelope.StateOnly).IsTrue()
      .Because("the requester's StateOnly intent (backfill vs repair) rides through to the bundles.");
  }

  [Test]
  public async Task Receptor_ClampsMaxEventsToTheOriginCapAsync() {
    var coordinator = new _selectingCoordinator();
    var transport = new _captureTransport();
    await using var sp = _buildProvider(coordinator, transport,
      options: new RedeliveryPumpOptions { MaxEventsPerRequest = 100 });
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestRedeliveryCommand {
      MaxEvents = 50_000,
      RequesterService = "svc",
      Topic = "t"
    });
    await Assert.That(coordinator.LastRequest!.MaxEvents).IsEqualTo(100)
      .Because("the origin owns its storm cap — a requester can never raise it.");

    await receptor.HandleAsync(new RequestRedeliveryCommand {
      MaxEvents = null,
      RequesterService = "svc",
      Topic = "t"
    });
    await Assert.That(coordinator.LastRequest!.MaxEvents).IsEqualTo(100)
      .Because("an unspecified request cap defaults to the origin's configured cap.");
  }

  [Test]
  public async Task Receptor_EmptySelection_PublishesNothingAsync() {
    var coordinator = new _selectingCoordinator();   // Selection stays empty
    var transport = new _captureTransport();
    await using var sp = _buildProvider(coordinator, transport);
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestRedeliveryCommand { RequesterService = "svc", Topic = "t" });

    await Assert.That(transport.Published.Count).IsEqualTo(0)
      .Because("nothing selected means nothing to repair — no empty bundles on the wire.");
  }

  [Test]
  public async Task Receptor_MissingInfrastructure_IsInertAsync() {
    var services = new ServiceCollection();   // no coordinator / transport / store / provider
    await using var sp = services.BuildServiceProvider();
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    // Must not throw — a host without the re-delivery infrastructure ignores the command.
    await receptor.HandleAsync(new RequestRedeliveryCommand { RequesterService = "svc", Topic = "t" });
  }

  [Test]
  public async Task Registrar_RegistersReceptorAtThreeDefaultStagesAsync() {
    var registry = new _recordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new RedeliveryRequestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Count).IsEqualTo(3);
    var stages = new HashSet<LifecycleStage>();
    foreach (var (msg, stage) in registry.Registered) {
      await Assert.That(msg).IsEqualTo(typeof(RequestRedeliveryCommand));
      stages.Add(stage);
    }
    await Assert.That(stages.Contains(LifecycleStage.LocalImmediateInline)).IsTrue();
    await Assert.That(stages.Contains(LifecycleStage.PreOutboxInline)).IsTrue();
    await Assert.That(stages.Contains(LifecycleStage.PostInboxInline)).IsTrue()
      .Because("A receptor without [FireAt] fires at all three default stages, so the command reaches it " +
               "in-process (operator) and over the inbox (damaged consumer).");
  }

  [Test]
  public async Task Registrar_NoRegistry_IsInertAsync() {
    var services = new ServiceCollection();   // no IReceptorRegistry
    await using var sp = services.BuildServiceProvider();
    var registrar = new RedeliveryRequestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);   // must not throw
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static ServiceProvider _buildProvider(
      _selectingCoordinator coordinator, _captureTransport transport,
      _captureSerializer? serializer = null, RedeliveryPumpOptions? options = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEventStore>(new _mapEventStore());
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    services.AddSingleton<IEnvelopeSerializer>(serializer ?? new _captureSerializer());
    if (options is not null) {
      services.AddSingleton(options);
    }
    return services.BuildServiceProvider();
  }

  private static RedeliveryEvent _evt(Guid streamId, Guid eventId, long version) => new() {
    EventId = eventId,
    StreamId = streamId,
    Version = version,
    CommitSequence = version,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
    Scope = null,
    Flags = 0
  };

  internal sealed record _probeEvent(Guid Id) : IEvent;

  /// <summary>Captures the selection request and returns a canned selection.</summary>
  private sealed class _selectingCoordinator : IWorkCoordinator {
    public RedeliveryRequest? LastRequest { get; private set; }
    public IReadOnlyList<RedeliveryEvent> Selection { get; set; } = [];
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;

    public Task<IReadOnlyList<RedeliveryEvent>> SelectRedeliveryEventsAsync(RedeliveryRequest request, CancellationToken cancellationToken = default) {
      LastRequest = request;
      return Task.FromResult(Selection);
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.CompletedTask;
  }

  /// <summary>Maps each raw row to an envelope whose MessageId is the row's EventId — the shape the
  /// real store's AOT deserialization path produces from stored envelopes.</summary>
  private sealed class _mapEventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      [.. streamEvents.Select(raw => new MessageEnvelope<IEvent> {
        MessageId = new MessageId(raw.EventId),
        Payload = new _probeEvent(raw.EventId),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
      })];

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(_probeEvent)];
  }

  /// <summary>Captures the typed composite envelope at the serializer seam and returns a
  /// field-copied JsonElement envelope, as the real serializer does.</summary>
  private sealed class _captureSerializer : IEnvelopeSerializer {
    public List<IMessageEnvelope<RedeliveryComposite>> Captured { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Captured.Add((IMessageEnvelope<RedeliveryComposite>)envelope);
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target,
          StateOnly = envelope.StateOnly
        },
        $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core",
        payloadType.AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class _recordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
