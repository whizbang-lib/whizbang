// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// End-to-end wiring locks for the bulk flush callbacks registered by
/// <see cref="WorkerPipelineExtensions.AddWhizbangWorkers"/>. The
/// <c>InboxBulkFlushCallback</c> / <c>OutboxBulkFlushCallback</c> closures resolve
/// <see cref="IWorkCoordinator"/> from a fresh DI scope per flush and call
/// <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/> /
/// <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>. If the wiring breaks (e.g.,
/// scope factory not used, wrong method called, partition count not propagated), the
/// strategy buffers messages but they never reach storage. The slice 7/12 registration
/// tests verify the strategies resolve to the right types but DON'T exercise the
/// callbacks themselves — these tests close that gap by going through Immediate
/// strategies (which flush synchronously, so no timing-dependence per
/// feedback_no_timing_tests).
/// </summary>
public class BatchStrategyFlushCallbackWiringTests {

  [Test]
  public async Task InboxBulkFlushCallback_ResolvesIWorkCoordinator_AndCallsStoreInboxMessagesAsync() {
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.Configure<WorkCoordinatorOptions>(opts => opts.PartitionCount = 7);
    services.AddWhizbangWorkers();
    services.AddWhizbangInboxStrategy<ImmediateInboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    // Mark schema ready — the flush callback awaits SchemaReadyGate before resolving the
    // coordinator. Without this the AppendAsync deadlocks (no migrations run in tests).
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    var strategy = sp.GetRequiredService<IInboxBatchStrategy>();

    var msgId = (Guid)TrackedGuid.New();
    var message = new InboxMessage {
      MessageId = msgId,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
    };

    await strategy.AppendAsync(message);
    await strategy.FlushAndStopAsync();

    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1)
      .Because("ImmediateInboxBatchStrategy must flush synchronously through the registered InboxBulkFlushCallback to StoreInboxMessagesAsync.");
    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1);
    await Assert.That(coordinator.StoredMessages[0].MessageId).IsEqualTo(msgId)
      .Because("The exact message must reach the coordinator — the callback closure must propagate the array.");
  }

  [Test]
  public async Task OutboxBulkFlushCallback_ResolvesIWorkCoordinator_AndCallsStoreOutboxMessagesAsync() {
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.Configure<WorkCoordinatorOptions>(opts => opts.PartitionCount = 9);
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    var strategy = sp.GetRequiredService<IOutboxBatchStrategy>();

    var msgId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonElement.Parse("{}"), []);
    var message = new OutboxMessage {
      MessageId = msgId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
    };

    await strategy.AppendAsync(message);
    await strategy.FlushAndStopAsync();

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("ImmediateOutboxBatchStrategy must flush synchronously through the registered OutboxBulkFlushCallback to StoreOutboxMessagesAsync.");
    await Assert.That(coordinator.LastOutboxPartitionCount).IsEqualTo(9)
      .Because("PartitionCount from WorkCoordinatorOptions must propagate through the callback factory to the coordinator call — covers a regression where the value is hardcoded or sourced from the wrong options.");
    await Assert.That(coordinator.StoredOutboxMessages.Count).IsEqualTo(1);
    await Assert.That(coordinator.StoredOutboxMessages[0].MessageId).IsEqualTo(msgId);
  }


  /// <summary>
  /// #1871: each per-stream batch flush takes a slot of the process-wide <see cref="WorkCoordinatorGate"/> around its
  /// store, so many streams flushing at once cannot open more connections than the gate allows. The gate records the
  /// slot it lent under the caller's name, which is the evidence the flush went through it.
  /// </summary>
  [Test]
  public async Task OutboxAndInboxBulkFlushCallbacks_StoreThroughAWorkCoordinatorGateSlotAsync() {
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(meterFactory: factory));
    using var recorded = new MetricAssertionHelper(factory.Create(WorkCoordinatorMetrics.METER_NAME));
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddLogging();
    // Registered before the pipeline, so the pipeline keeps it.
    services.AddSingleton(new WorkCoordinatorGate(4, NullLogger<WorkCoordinatorGate>.Instance, metrics: metrics));
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();
    services.AddWhizbangInboxStrategy<ImmediateInboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    var outboxId = (Guid)TrackedGuid.New();
    await sp.GetRequiredService<IOutboxBatchStrategy>().AppendAsync(new OutboxMessage {
      MessageId = outboxId,
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(outboxId), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(outboxId), Hops = [] },
    });
    var inboxId = (Guid)TrackedGuid.New();
    await sp.GetRequiredService<IInboxBatchStrategy>().AppendAsync(new InboxMessage {
      MessageId = inboxId,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(inboxId),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(inboxId), Hops = [] },
    });

    var callers = recorded.GetByName("whizbang.gate.hold_duration_ms").ConvertAll(m => m.Tags["caller"]);
    await Assert.That(callers).Contains("OutboxBatchFlush")
      .Because("the outbox batch's store held a gate slot");
    await Assert.That(callers).Contains("InboxBatchFlush")
      .Because("the inbox batch's store held a gate slot");
    await Assert.That(coordinator.StoredOutboxMessages.Count).IsEqualTo(1);
    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// #1885: the registered inbox batcher hands a batch it could not store to the registered failure recorder, which
  /// writes it to the dead-letter table through the coordinator resolved from a fresh scope.
  /// </summary>
  [Test]
  public async Task SlidingWindowInboxBatchStrategy_ABatchItCannotStore_IsDeadLetteredThroughTheRegisteredRecorderAsync() {
    var coordinator = new RefusingInboxCoordinator();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.Configure<SlidingWindowInboxOptions>(opts => opts.MaxSize = 1);
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    var strategy = sp.GetRequiredService<IInboxBatchStrategy>();
    var msgId = (Guid)TrackedGuid.New();
    await strategy.AppendAsync(new InboxMessage {
      MessageId = msgId,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.New(),
    });
    await strategy.FlushAndStopAsync();

    await Assert.That(strategy).IsTypeOf<SlidingWindowInboxBatchStrategy>();
    var (messages, request) = coordinator.DeadLettered.Single();
    await Assert.That(messages.Select(m => m.MessageId)).IsEquivalentTo([msgId]);
    await Assert.That(request.FailureReason).IsEqualTo(MessageFailureReason.InboxStoreFailed);
    await Assert.That(request.Attempts).IsEqualTo(1)
      .Because("a store refused with an error a retry cannot fix is given up after its first attempt");
  }

  /// <summary>Refuses every inbox store with an error a retry cannot fix, and keeps what it is asked to dead-letter.</summary>
  private sealed class RefusingInboxCoordinator : IWorkCoordinator {
    private readonly System.Collections.Concurrent.ConcurrentQueue<(InboxMessage[] Messages, InboxDeadLetterRequest Request)> _deadLettered = new();

    public IReadOnlyList<(InboxMessage[] Messages, InboxDeadLetterRequest Request)> DeadLettered => [.. _deadLettered];

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromException(new InvalidOperationException("the inbox store refused the batch"));

    public Task<bool> StoreInboxDeadLettersAsync(InboxMessage[] messages, InboxDeadLetterRequest request, CancellationToken cancellationToken = default) {
      _deadLettered.Enqueue((messages, request));
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
