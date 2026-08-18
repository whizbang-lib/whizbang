using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks coalesce-group stamping at the outbox mint seams: every message entering
/// <see cref="WorkCoordinatorQueues.AddOutboxMessage"/> (the Immediate/Scoped path — including
/// the audit companion it builds), the <see cref="StreamAffinityWorkCoordinatorStrategy"/>
/// batcher path, and the deferred-channel drain runs through
/// <see cref="CoalesceGroupResolver.ApplyCoalescePolicy"/>. Stamping at mint is what makes a
/// pending single durable-in-the-same-transaction yet invisible to the claim pump.
/// </summary>
[Category("Core")]
public class CoalesceMintStampingTests {
  private static readonly DateTimeOffset _testNow = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

  #region WorkCoordinatorQueues.AddOutboxMessage (Immediate/Scoped seam)

  [Test]
  public async Task AddOutboxMessage_BoundTag_StampsGroupAndFloorAsync() {
    var time = new FakeTimeProvider(_testNow);
    var queues = new WorkCoordinatorQueues(logger: null, coalesceResolver: _resolver(time, out _));

    queues.AddOutboxMessage(_taggedMessage(), systemEventOptions: null);

    await Assert.That(queues.OutboxMessages[0].CoalesceGroup).IsEqualTo("record-digest");
    await Assert.That(queues.OutboxMessages[0].ScheduledFor).IsEqualTo(_testNow.AddSeconds(120));
  }

  [Test]
  public async Task AddOutboxMessage_UnboundType_QueuesUntouchedAsync() {
    var time = new FakeTimeProvider(_testNow);
    var queues = new WorkCoordinatorQueues(logger: null, coalesceResolver: _resolver(time, out _));

    queues.AddOutboxMessage(_untaggedMessage(), systemEventOptions: null);

    await Assert.That(queues.OutboxMessages[0].CoalesceGroup).IsNull();
    await Assert.That(queues.OutboxMessages[0].ScheduledFor).IsNull();
  }

  [Test]
  public async Task AddOutboxMessage_NoResolver_BehavesExactlyAsBeforeAsync() {
    var queues = new WorkCoordinatorQueues();

    queues.AddOutboxMessage(_taggedMessage(), systemEventOptions: null);

    await Assert.That(queues.OutboxMessages[0].CoalesceGroup).IsNull();
    await Assert.That(queues.OutboxMessages[0].ScheduledFor).IsNull();
  }

  [Test]
  public async Task AddOutboxMessage_AuditCompanion_FlowsThroughTheSameGenericPathAsync() {
    // The audit companion built inside AddOutboxMessage must ride the SAME generic stamping
    // path as any other message. Bind EventAudited to a group here (what EnableAudit() will do
    // built-in) with the builder's own floor bypassed (slide = 0), and the companion comes out
    // group-stamped by the resolver, not by audit-specific code.
    var time = new FakeTimeProvider(_testNow);
    var tagOptions = new TagOptions();
    tagOptions.Coalesce(SystemTags.AUDIT, c => c.MaxDelaySeconds = 120);
    var resolver = new CoalesceGroupResolver(tagOptions, time, () => [
      CoalesceGroupResolverTests.TagRegistration(typeof(TestTaggedEvent), "record-digest"),
      CoalesceGroupResolverTests.TagRegistration(typeof(EventAudited), SystemTags.AUDIT)
    ]);
    var systemEventOptions = new SystemEventOptions { AuditShipSlideSeconds = 0 };
    systemEventOptions.EnableEventAudit();
    var queues = new WorkCoordinatorQueues(logger: null, coalesceResolver: resolver);

    queues.AddOutboxMessage(_untaggedMessage(), systemEventOptions);

    await Assert.That(queues.PendingAuditMessages.Count).IsEqualTo(1);
    await Assert.That(queues.PendingAuditMessages[0].CoalesceGroup).IsEqualTo(SystemTags.AUDIT);
    await Assert.That(queues.PendingAuditMessages[0].ScheduledFor).IsEqualTo(_testNow.AddSeconds(120));
  }

  #endregion

  #region StreamAffinityWorkCoordinatorStrategy seam

  [Test]
  public async Task StreamAffinity_QueueOutboxMessageAsync_StampsBeforeAppendAsync() {
    var time = new FakeTimeProvider(_testNow);
    var batch = new RecordingBatchStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(
      new RecordingInner(), batch, systemEventOptions: null, logger: null,
      coalesceResolver: _resolver(time, out _));

    await sut.QueueOutboxMessageAsync(_taggedMessage());

    await Assert.That(batch.Appended.Count).IsEqualTo(1);
    await Assert.That(batch.Appended[0].CoalesceGroup).IsEqualTo("record-digest");
    await Assert.That(batch.Appended[0].ScheduledFor).IsEqualTo(_testNow.AddSeconds(120));
  }

  [Test]
  public async Task StreamAffinity_NoResolver_AppendsUntouchedAsync() {
    var batch = new RecordingBatchStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(new RecordingInner(), batch);

    await sut.QueueOutboxMessageAsync(_taggedMessage());

    await Assert.That(batch.Appended[0].CoalesceGroup).IsNull();
  }

  #endregion

  #region Deferred-channel drain seam (Immediate strategy)

  [Test]
  public async Task ImmediateFlush_DeferredMessages_AreStampedOnDrainAsync() {
    // The deferred channel bypasses AddOutboxMessage (events published outside transaction
    // context, incl. the audit event-store decorator's mints) — the drain into the flush is
    // therefore a mint seam of its own and must stamp too.
    var time = new FakeTimeProvider(_testNow);
    var deferredChannel = new DeferredOutboxChannel();
    await deferredChannel.QueueAsync(_taggedMessage());
    var coordinator = new CapturingCoordinator();
    var strategy = new ImmediateWorkCoordinatorStrategy(
      coordinator,
      new ServiceInstanceProvider(configuration: null),
      new WorkCoordinatorOptions(),
      deferredChannel: deferredChannel,
      coalesceResolver: _resolver(time, out _));

    await strategy.FlushAsync(WorkBatchOptions.None);

    await Assert.That(coordinator.LastStoredOutbox).IsNotNull();
    await Assert.That(coordinator.LastStoredOutbox![0].CoalesceGroup).IsEqualTo("record-digest");
    await Assert.That(coordinator.LastStoredOutbox[0].ScheduledFor).IsEqualTo(_testNow.AddSeconds(120));
  }

  #endregion

  #region Helpers

  private static CoalesceGroupResolver _resolver(FakeTimeProvider time, out TagOptions tagOptions) {
    var options = new TagOptions();
    options.Coalesce("record-digest", c => c.MaxDelaySeconds = 120);
    tagOptions = options;
    return new CoalesceGroupResolver(options, time,
      () => [CoalesceGroupResolverTests.TagRegistration(typeof(TestTaggedEvent), "record-digest")]);
  }

  private static OutboxMessage _taggedMessage() => _message(typeof(TestTaggedEvent).AssemblyQualifiedName!);

  private static OutboxMessage _untaggedMessage() => _message(typeof(TestUntaggedEvent).AssemblyQualifiedName!);

  private static OutboxMessage _message(string messageType) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { test = "data" }),
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      MessageType = messageType
    };
  }

  internal sealed record TestTaggedEvent : IEvent;

  internal sealed record TestUntaggedEvent : IEvent;

  private sealed class RecordingBatchStrategy : IOutboxBatchStrategy {
    public List<OutboxMessage> Appended { get; } = [];

    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
      Appended.Add(message);
      return ValueTask.CompletedTask;
    }

    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class RecordingInner : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class CapturingCoordinator : IWorkCoordinator {
    public OutboxMessage[]? LastStoredOutbox { get; private set; }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      LastStoredOutbox = messages;
      return Task.CompletedTask;
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  #endregion
}
