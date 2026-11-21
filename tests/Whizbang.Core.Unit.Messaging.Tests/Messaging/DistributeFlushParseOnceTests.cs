// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A flush reads each message's payload once for all five Distribute stages (#1526, plans/message-cache.md step 1).
/// Before, every stage parsed the stored payload again: five parses of the same bytes for every message of every flush.
/// </summary>
/// <remarks>
/// The parsed payload is shared by the stages of one flush only. Each stage still gets its own envelope, so hops and
/// stamps one stage adds are never seen by another, and nothing outlives the flush.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/LifecycleInvocationHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/FlushPayloads.cs</code-under-test>
[Category("Messaging")]
[Category("Lifecycle")]
public class DistributeFlushParseOnceTests {
  private const string MESSAGE_TYPE = "ParseOnceProbe, ProbeAssembly";

  private static readonly LifecycleStage[] _distributeStages = [
    LifecycleStage.PreDistributeDetached,
    LifecycleStage.PreDistributeInline,
    LifecycleStage.DistributeDetached,
    LifecycleStage.PostDistributeDetached,
    LifecycleStage.PostDistributeInline,
  ];

  [Test]
  public async Task OneFlush_FiveDistributeStages_ParseEachPayloadOnceAsync() {
    var outbox = _outbox();
    var inbox = _inbox();
    var deserializer = new CountingDeserializer();
    var invoker = new RecordingInvoker();
    await using var provider = _provider(invoker);
    using var recorder = new MessageParseRecorder(outbox.MessageId, inbox.MessageId);
    var context = _context(provider, deserializer, [outbox], [inbox]);

    await _runFiveStagesAsync(context);

    await Assert.That(deserializer.CallsFor(outbox.MessageId)).IsEqualTo(1)
      .Because("the five stages of one flush read the outbox message's payload once and share it");
    await Assert.That(deserializer.CallsFor(inbox.MessageId)).IsEqualTo(1)
      .Because("the five stages of one flush read the inbox message's payload once and share it");
    await Assert.That(recorder.CountFor(outbox.MessageId, MessageParseKind.Payload)).IsEqualTo(1)
      .Because("the parse seam counts the one parse, not one per stage");
    await Assert.That(recorder.CountFor(inbox.MessageId, MessageParseKind.Payload)).IsEqualTo(1);
    await Assert.That(invoker.StagesFor(outbox.MessageId)).IsEquivalentTo(_distributeStages)
      .Because("parsing once must not skip a stage: every stage still runs its receptors");
    await Assert.That(invoker.StagesFor(inbox.MessageId)).IsEquivalentTo(_distributeStages);
  }

  [Test]
  public async Task OneFlush_EveryStageGetsTheSamePayload_InItsOwnEnvelopeAsync() {
    var outbox = _outbox();
    var invoker = new RecordingInvoker();
    await using var provider = _provider(invoker);
    var context = _context(provider, new CountingDeserializer(), [outbox], []);

    await _runFiveStagesAsync(context);

    var invocations = invoker.For(outbox.MessageId);
    await Assert.That(invocations.Select(i => i.Envelope.Payload).Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(1)
      .Because("the stages share the one payload the flush parsed");
    await Assert.That(invocations.Select(i => i.Envelope).Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(5)
      .Because("each stage gets an envelope of its own, so hops and stamps it adds are never seen by another stage");
  }

  [Test]
  public async Task TwoFlushes_OfTheSameMessage_EachParseItAsync() {
    var outbox = _outbox();
    var deserializer = new CountingDeserializer();
    await using var provider = _provider(new RecordingInvoker());

    await _runFiveStagesAsync(_context(provider, deserializer, [outbox], []));
    await _runFiveStagesAsync(_context(provider, deserializer, [outbox], []));

    await Assert.That(deserializer.CallsFor(outbox.MessageId)).IsEqualTo(2)
      .Because("a parsed payload is held for its flush only; nothing is kept from one flush to the next");
  }

  [Test]
  public async Task OneFlush_TwoEnvelopesWithTheSameMessageId_AreEachParsedAsync() {
    var first = _outbox();
    // The same id with another body (an upcast, a repair, a rehydrated offload): the id alone never identifies a payload.
    var second = first with { Envelope = _envelope(MessageId.From(first.MessageId), "another body") };
    var deserializer = new CountingDeserializer();
    var invoker = new RecordingInvoker();
    await using var provider = _provider(invoker);

    await _runFiveStagesAsync(_context(provider, deserializer, [first, second], []));

    await Assert.That(deserializer.CallsFor(first.MessageId)).IsEqualTo(2)
      .Because("each envelope is parsed once, and two envelopes are two parses even when they carry one message id");
    var bodies = invoker.For(first.MessageId).Select(i => ((ParseOnceProbe)i.Envelope.Payload).Body).Distinct().ToList();
    await Assert.That(bodies).IsEquivalentTo(["body", "another body"])
      .Because("each envelope's stages see the payload read from that envelope, never the other one's");
  }

  [Test]
  public async Task OneFlush_APayloadThatFailedToParse_IsReadAgainByTheNextStageAsync() {
    var outbox = _outbox();
    var deserializer = new CountingDeserializer(failFirst: 1);
    var invoker = new RecordingInvoker();
    await using var provider = _provider(invoker);
    var context = _context(provider, deserializer, [outbox], []);

    // The pre-store stages fail for this message: nothing is invoked and, by default, the inline stage throws.
    await Assert.That(async () => await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PreDistributeDetached, LifecycleStage.PreDistributeInline, context))
      .ThrowsExactly<JsonException>();
    await context.DetachedWork!.DrainAsync(CancellationToken.None);
    var parsesBefore = deserializer.CallsFor(outbox.MessageId);

    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline, context);
    await context.DetachedWork!.DrainAsync(CancellationToken.None);

    await Assert.That(deserializer.CallsFor(outbox.MessageId)).IsEqualTo(parsesBefore + 1)
      .Because("a failure is never remembered: the next stage reads the payload again, and then shares what it read");
    await Assert.That(invoker.StagesFor(outbox.MessageId)).Contains(LifecycleStage.PostDistributeInline)
      .Because("a stage after a failed read still runs once the payload can be read");
  }

  [Test]
  public async Task FlushHelper_OneFlush_ParsesEachPayloadOnceAsync() {
    var outbox = _outbox();
    var deserializer = new CountingDeserializer();
    var invoker = new RecordingInvoker();
    var coordinator = new Whizbang.Core.Tests.Workers.NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddSingleton<IWorkCoordinator>(coordinator);
    await using var provider = services.BuildServiceProvider();
    var detached = new DetachedLifecycleWork();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(new FlushContext(
      Coordinator: null,
      ScopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      InstanceProvider: new FakeInstanceProvider(),
      Options: new WorkCoordinatorOptions { PartitionCount = 4 },
      OutboxMessages: [outbox],
      InboxMessages: [],
      OutboxCompletions: [],
      InboxCompletions: [],
      OutboxFailures: [],
      InboxFailures: [],
      LifecycleMessageDeserializer: deserializer,
      Logger: NullLogger.Instance,
      TracingOptions: null,
      LifecycleMetrics: null,
      WorkChannelWriter: null,
      PendingAuditMessages: null,
      DetachedWork: detached), default);
    await detached.DrainAsync(CancellationToken.None);

    await Assert.That(deserializer.CallsFor(outbox.MessageId)).IsEqualTo(1)
      .Because("the flush hands its five Distribute stages one parse of each payload, without the caller doing anything");
    await Assert.That(invoker.StagesFor(outbox.MessageId)).IsEquivalentTo(_distributeStages);
  }

  // ========================================
  // Helpers
  // ========================================

  /// <summary>The five Distribute stages of one flush, as <c>WorkCoordinatorFlushHelper</c> runs them.</summary>
  private static async Task _runFiveStagesAsync(DistributeLifecycleContext context) {
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PreDistributeDetached, LifecycleStage.PreDistributeInline, context);
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(LifecycleStage.DistributeDetached, context);
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeDetached, LifecycleStage.PostDistributeInline,
      context with { InlineFailures = InlineStageFailureHandling.RunEveryReceptorThenThrowFirst });
    await context.DetachedWork!.DrainAsync(CancellationToken.None);
  }

  private static ServiceProvider _provider(IReceptorInvoker invoker) {
    var services = new ServiceCollection();
    services.AddScoped(_ => invoker);
    return services.BuildServiceProvider();
  }

  private static DistributeLifecycleContext _context(
      ServiceProvider provider, ILifecycleMessageDeserializer deserializer, OutboxMessage[] outbox, InboxMessage[] inbox) =>
    new(outbox, inbox, provider.GetRequiredService<IServiceScopeFactory>(), deserializer, NullLogger.Instance,
      EnableLifecycleTracing: false, DetachedWork: new DetachedLifecycleWork());

  private static MessageEnvelope<JsonElement> _envelope(MessageId messageId, string body = "body") {
    using var document = JsonDocument.Parse($"{{\"id\":\"{messageId.Value}\",\"body\":\"{body}\"}}");
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = document.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static OutboxMessage _outbox() {
    var messageId = MessageId.New();
    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = "parse-once-topic",
      Envelope = _envelope(messageId),
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[ParseOnceProbe, ProbeAssembly]]",
      MessageType = MESSAGE_TYPE
    };
  }

  private static InboxMessage _inbox() {
    var messageId = MessageId.New();
    return new InboxMessage {
      MessageId = messageId.Value,
      HandlerName = "ParseOnceHandler",
      Envelope = _envelope(messageId),
      EnvelopeType = "MessageEnvelope`1[[ParseOnceProbe, ProbeAssembly]]",
      MessageType = MESSAGE_TYPE
    };
  }

  private sealed record ParseOnceProbe(string Body) : IMessage;

  /// <summary>
  /// Reads each payload as a new <see cref="ParseOnceProbe"/> and counts the reads per message id. The first
  /// <c>failFirst</c> reads fail with a <see cref="JsonException"/>.
  /// </summary>
  private sealed class CountingDeserializer(int failFirst = 0) : ILifecycleMessageDeserializer {
    private readonly ConcurrentDictionary<Guid, int> _calls = new();
    private int _failuresLeft = failFirst;

    public int CallsFor(Guid messageId) => _calls.GetValueOrDefault(messageId);

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => throw new NotSupportedException();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => throw new NotSupportedException();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => throw new NotSupportedException();

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      var messageId = jsonElement.GetProperty("id").GetGuid();
      _calls.AddOrUpdate(messageId, 1, static (_, count) => count + 1);
      if (Interlocked.Decrement(ref _failuresLeft) >= 0) {
        throw new JsonException($"cannot read {messageId}");
      }
      return new ParseOnceProbe(jsonElement.GetProperty("body").GetString()!);
    }
  }

  /// <summary>Records every (stage, envelope) it is asked to invoke.</summary>
  private sealed class RecordingInvoker : IReceptorInvoker {
    private readonly ConcurrentQueue<(LifecycleStage Stage, IMessageEnvelope Envelope)> _invocations = new();

    public List<(LifecycleStage Stage, IMessageEnvelope Envelope)> For(Guid messageId) =>
      [.. _invocations.Where(i => i.Envelope.MessageId.Value == messageId)];

    public List<LifecycleStage> StagesFor(Guid messageId) => [.. For(messageId).Select(i => i.Stage)];

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      _invocations.Enqueue((stage, envelope));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "ParseOnceService";
    public string HostName => "parse-once-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
