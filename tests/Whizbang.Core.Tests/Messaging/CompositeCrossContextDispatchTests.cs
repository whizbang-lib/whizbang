// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Issue #938: a consumer that handles only some of a composite's inner event types stored the
/// composite (#915) and then lost it at dispatch. The composite was written by a publisher that knows
/// every inner type, stored by the consumer in a <c>jsonb</c> inbox column, and read back by the
/// dispatch deserializer with the consumer's own <see cref="JsonSerializerOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two things met. <c>jsonb</c> orders an object's keys by length, so an inner element with a key of
/// four characters or fewer comes back with <c>$type</c> no longer first. And the options the consumer
/// registered were its generated <c>WhizbangJsonContext.CreateOptions()</c>, which chain four fixed
/// contexts and do not allow out-of-order metadata. STJ read the element as the base type, created the
/// unresolved-element placeholder, then met <c>$type</c> as an ordinary property and refused the whole
/// composite ("The metadata property is either not supported by the type or is not the first property").
/// Every generated message type binds through a parameterized constructor with more than four
/// arguments, so the failure surfaced in the large parameterized-constructor converter whether the
/// composite's <c>Inner</c> is constructor-bound or init-only.
/// </para>
/// <para>
/// Each test mirrors the real shape: the publisher serializes with the full registry, the consumer's
/// transport reads that wire form, the consumer's host options write the inbox row, the row is key-ordered
/// the way <c>jsonb</c> stores it, and the dispatch worker reads it with the host options.
/// </para>
/// </remarks>
[Category("Messaging")]
[Category("JsonSerialization")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class CompositeCrossContextDispatchTests {

  /// <summary>A composite whose inner list is bound through its public constructor.</summary>
  public sealed class CtorBoundCrossContextComposite : CompositeEventBase {
    /// <summary>Binds every member, so the generated metadata constructs through this constructor.</summary>
    public CtorBoundCrossContextComposite(
        Guid streamId, List<IMessage> inner, int maxInnerEventsAllowed, FanoutMode fanoutMode, FanoutAtomicity atomicity) {
      StreamId = streamId;
      Inner = inner;
      MaxInnerEventsAllowed = maxInnerEventsAllowed;
      FanoutMode = fanoutMode;
      Atomicity = atomicity;
    }
  }

  /// <summary>A composite whose inner list is set through its init accessor.</summary>
  public sealed class SettableCrossContextComposite : CompositeEventBase;

  /// <summary>An inner event this consumer handles. Its short keys are what <c>jsonb</c> moves ahead of <c>$type</c>.</summary>
  public sealed record ShortKeyHandledEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Note { get; init; } = string.Empty;
    public int Seq { get; init; }
  }

  /// <summary>An inner event the publisher knows and this consumer does not; its discriminator is rewritten on the wire.</summary>
  public sealed record PublisherOnlyEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Note { get; init; } = string.Empty;
  }

  private const string UNKNOWN_DISCRIMINATOR = "Publisher.Contracts.EventThisConsumerNeverReferenced";

  // ------------------------------------------------------------------------------------------
  // Reading the stored form
  // ------------------------------------------------------------------------------------------

  [Test]
  public async Task Deserialize_CtorBoundInner_StoredByHostOptions_KeepsResolvableAndPlaceholdersTheRestAsync() {
    var streamId = Guid.CreateVersion7();
    var stored = _storedInboxPayload(new CtorBoundCrossContextComposite(
      streamId, _innerEvents(streamId), 10_000, FanoutMode.Auto, FanoutAtomicity.Independent), out var messageType);

    var composite = (CtorBoundCrossContextComposite)new JsonLifecycleMessageDeserializer(_hostOptions())
      .DeserializeFromJsonElement(stored, messageType);

    await _assertInnerAsync(composite.Inner);
  }

  [Test]
  public async Task Deserialize_SettableInner_StoredByHostOptions_KeepsResolvableAndPlaceholdersTheRestAsync() {
    var streamId = Guid.CreateVersion7();
    var stored = _storedInboxPayload(
      new SettableCrossContextComposite { StreamId = streamId, Inner = _innerEvents(streamId) }, out var messageType);

    var composite = (SettableCrossContextComposite)new JsonLifecycleMessageDeserializer(_hostOptions())
      .DeserializeFromJsonElement(stored, messageType);

    await _assertInnerAsync(composite.Inner);
  }

  // ------------------------------------------------------------------------------------------
  // Dispatching the stored form
  // ------------------------------------------------------------------------------------------

  [Test]
  public async Task Dispatch_CtorBoundCrossContextComposite_FansOutResolvableInnerEventsOnlyAsync() {
    var streamId = Guid.CreateVersion7();
    var stored = _storedInboxPayload(new CtorBoundCrossContextComposite(
      streamId, _innerEvents(streamId), 10_000, FanoutMode.Auto, FanoutAtomicity.Independent), out var messageType);

    await _assertDispatchFansOutHandledOnlyAsync(stored, messageType, streamId);
  }

  [Test]
  public async Task Dispatch_SettableCrossContextComposite_FansOutResolvableInnerEventsOnlyAsync() {
    var streamId = Guid.CreateVersion7();
    var stored = _storedInboxPayload(
      new SettableCrossContextComposite { StreamId = streamId, Inner = _innerEvents(streamId) }, out var messageType);

    await _assertDispatchFansOutHandledOnlyAsync(stored, messageType, streamId);
  }

  // ------------------------------------------------------------------------------------------

  private static List<IMessage> _innerEvents(Guid streamId) => [
    new ShortKeyHandledEvent { StreamId = streamId, Note = "first", Seq = 1 },
    new PublisherOnlyEvent { StreamId = streamId, Note = "unknown here" },
    new ShortKeyHandledEvent { StreamId = streamId, Note = "second", Seq = 2 },
  ];

  private static async Task _assertInnerAsync(List<IMessage> inner) {
    await Assert.That(inner.Count).IsEqualTo(3)
      .Because("an element this consumer cannot resolve must not fail the whole composite, and a reordered discriminator must still be read");
    await Assert.That(inner[0]).IsTypeOf<ShortKeyHandledEvent>();
    await Assert.That(((ShortKeyHandledEvent)inner[0]).Seq).IsEqualTo(1);
    await Assert.That(inner[1]).IsTypeOf<UnresolvedMessage>();
    await Assert.That(inner[2]).IsTypeOf<ShortKeyHandledEvent>();
    await Assert.That(((ShortKeyHandledEvent)inner[2]).Note).IsEqualTo("second");
  }

  private static async Task _assertDispatchFansOutHandledOnlyAsync(JsonElement stored, string messageType, Guid streamId) {
    var hostOptions = _hostOptions();
    var commits = new CapturingCommitChannel();
    var failures = new CapturingFailureChannel();
    var deadLetters = new CapturingDeadLetterStore();
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(hostOptions))
      .BuildServiceProvider();
    var worker = _worker(sp, new JsonLifecycleMessageDeserializer(hostOptions), commits, failures, deadLetters);
    var work = _work(stored, messageType, streamId);

    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(deadLetters.Moves).IsEmpty()
      .Because("a composite whose resolvable inner events can be read is fanned out, not dead-lettered");
    var commit = commits.All.Single();
    await Assert.That(commit.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored);
    var children = commit.NewInboxMessages!;
    await Assert.That(children.Count).IsEqualTo(2)
      .Because("both handled inner events become child rows; the one this consumer cannot name is skipped as unsubscribed");
    await Assert.That(children.All(c => c.MessageType.Contains(nameof(ShortKeyHandledEvent), StringComparison.Ordinal))).IsTrue();
    await Assert.That(failures.All).IsEmpty();
  }

  /// <summary>
  /// The options a consumer registers when it follows the generated facade: four fixed contexts, no
  /// registry fallback, no out-of-order metadata. The same shape the deployed consumer's errors named.
  /// </summary>
  private static JsonSerializerOptions _hostOptions() => Whizbang.Core.Tests.Generated.WhizbangJsonContext.CreateOptions();

  /// <summary>
  /// Publisher writes with every context; the consumer's transport reads the wire with every context;
  /// the consumer's host options write the inbox row; <c>jsonb</c> reorders the row's keys.
  /// </summary>
  private static JsonElement _storedInboxPayload<TComposite>(TComposite composite, out string messageType)
      where TComposite : CompositeEventBase {
    var publisher = JsonContextRegistry.CreateCombinedOptions();
    var wire = JsonSerializer.Serialize(composite, publisher.GetTypeInfo(typeof(TComposite)));
    var discriminator = typeof(PublisherOnlyEvent).FullName!.Replace('+', '.');
    if (!wire.Contains(discriminator, StringComparison.Ordinal)) {
      throw new InvalidOperationException($"Fixture assumption broken: discriminator '{discriminator}' not in {wire}");
    }
    // This consumer does not reference the publisher-only type: on its side the discriminator names nothing.
    wire = wire.Replace(discriminator, UNKNOWN_DISCRIMINATOR, StringComparison.Ordinal);

    var received = (TComposite)JsonSerializer.Deserialize(wire, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(TComposite)))!;
    var envelope = new MessageEnvelope<TComposite> {
      MessageId = MessageId.New(),
      Payload = received,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var row = new EnvelopeSerializer(_hostOptions()).SerializeEnvelope(envelope);
    messageType = row.MessageType;

    var jsonb = JsonbKeyOrderSimulator.Normalize(row.JsonEnvelope.Payload.GetRawText());
    if (jsonb.IndexOf("\"Note\"", StringComparison.Ordinal) > jsonb.IndexOf("\"$type\"", StringComparison.Ordinal)) {
      throw new InvalidOperationException($"Fixture assumption broken: jsonb order kept $type first in {jsonb}");
    }
    return JsonDocument.Parse(jsonb).RootElement.Clone();
  }

  private static InboxWork _work(JsonElement payload, string messageType, Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = payload,
        Hops = [new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()),
          },
        }],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = messageType,
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private static InboxDispatchWorker _worker(
      IServiceProvider sp, ILifecycleMessageDeserializer deserializer,
      CapturingCommitChannel commits, CapturingFailureChannel failures, IDeadLetterStore deadLetters) {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new InboxDispatchWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new FakeInstanceProvider(),
      inboxChannelWriter: new FakeInboxChannelWriter(),
      handlerCommitChannel: commits,
      failureChannel: failures,
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions()),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: deserializer,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: deadLetters,
      generationProvider: new DefaultGenerationProvider());
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 11;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class CapturingCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentQueue<HandlerCommitRequest> All { get; } = new();
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      All.Enqueue(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CapturingFailureChannel : IFailureChannel {
    public ConcurrentQueue<MessageFailure> All { get; } = new();
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentQueue<Guid> Moves { get; } = new();
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Enqueue(sourceId);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }
}
