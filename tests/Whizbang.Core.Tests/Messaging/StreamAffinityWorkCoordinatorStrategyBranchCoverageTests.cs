// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="StreamAffinityWorkCoordinatorStrategy.QueueOutboxMessageAsync"/>
/// with both event audit and a coalesce resolver wired: the audit companion this route builds is a
/// mint of its own, so it passes through the resolver too, and a coalesce binding on the audit
/// type stamps it before it reaches the batcher.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamAffinityWorkCoordinatorStrategy.cs</code-under-test>
public class StreamAffinityWorkCoordinatorStrategyBranchCoverageTests {
  private static readonly DateTimeOffset _testNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task QueueOutboxMessageAsync_AuditedEventWithResolver_StampsTheAuditCompanionAsync() {
    var time = new FakeTimeProvider(_testNow);
    var tagOptions = new TagOptions();
    tagOptions.Coalesce("audit-digest", c => c.MaxDelaySeconds = 60);
    var resolver = new CoalesceGroupResolver(tagOptions, time,
      () => [CoalesceGroupResolverTests.TagRegistration(typeof(EventAudited), "audit-digest")]);
    var auditOptions = new SystemEventOptions();
    auditOptions.EnableEventAudit();
    var batch = new RecordingBatchStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(
      inner: new RecordingInner(),
      outboxBatch: batch,
      logger: NullLogger.Instance,
      systemEventOptions: auditOptions,
      coalesceResolver: resolver);
    var domainEvent = _eventOutboxMessage();

    await sut.QueueOutboxMessageAsync(domainEvent);

    await Assert.That(batch.Appended.Count).IsEqualTo(2)
      .Because("an audited event rides the batch together with its audit companion");
    var appendedEvent = batch.Appended.Single(m => m.MessageId == domainEvent.MessageId);
    var audit = batch.Appended.Single(m => m.MessageId != domainEvent.MessageId);
    await Assert.That(audit.MessageType).Contains(nameof(EventAudited));
    await Assert.That(audit.CoalesceGroup).IsEqualTo("audit-digest")
      .Because("the audit companion is minted on this route, so the resolver must stamp it here");
    await Assert.That(audit.ScheduledFor).IsEqualTo(_testNow.AddSeconds(60));
    await Assert.That(appendedEvent.CoalesceGroup).IsNull()
      .Because("the domain event's own type has no binding, so the resolver leaves it as it was");
  }

  private static OutboxMessage _eventOutboxMessage() {
    var messageId = TrackedGuid.New().Value;
    using var document = JsonDocument.Parse("{}");
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), document.RootElement.Clone(), []);
    return new OutboxMessage {
      MessageId = messageId,
      StreamId = TrackedGuid.New().Value,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
      IsEvent = true,
    };
  }

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
}
