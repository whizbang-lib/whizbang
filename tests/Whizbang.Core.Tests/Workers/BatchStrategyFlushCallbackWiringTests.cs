using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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

    var msgId = (Guid)TrackedGuid.NewMedo();
    var message = new InboxMessage {
      MessageId = msgId,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
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

    var msgId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonDocument.Parse("{}").RootElement, []);
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
  /// Regression lock for the bug that surfaced on feat/work-pump-decomposition: when
  /// <see cref="StreamAffinityWorkCoordinatorStrategy"/> intercepts QueueOutboxMessageAsync to
  /// route through <see cref="IOutboxBatchStrategy"/>, the dispatcher's downstream
  /// strategy.FlushAsync() sees an empty scoped-strategy queue and skips the lifecycle helper
  /// entirely. The fix moved Distribute lifecycle invocation into
  /// <c>OutboxBulkFlushCallback</c> itself so receptors at PreDistribute*, DistributeDetached,
  /// and PostDistribute* still fire for outbox messages routed through the batcher.
  /// </summary>
  [Test]
  public async Task OutboxBulkFlushCallback_FiresPostDistributeInlineLifecycleAsync() {
    var coordinator = new NoOpWorkCoordinator();
    var deserializer = new CapturingLifecycleMessageDeserializer();
    var invoker = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ILifecycleMessageDeserializer>(deserializer);
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.Configure<WorkCoordinatorOptions>(opts => opts.PartitionCount = 1);
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    var strategy = sp.GetRequiredService<IOutboxBatchStrategy>();

    var msgId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonDocument.Parse("{}").RootElement, []);
    var message = new OutboxMessage {
      MessageId = msgId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
    };

    await strategy.AppendAsync(message);
    await strategy.FlushAndStopAsync();

    var postInline = invoker.Invocations.Where(i => i.Stage == LifecycleStage.PostDistributeInline).ToList();
    await Assert.That(postInline.Count).IsEqualTo(1)
      .Because("PostDistributeInline MUST fire inside the OutboxBulkFlushCallback. The StreamAffinity batch path bypasses ScopedWorkCoordinatorStrategy's queue, so without lifecycle invocation here, no receptor registered at PostDistributeInline ever fires for outbox messages.");

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("The store call must happen before the Post-Distribute lifecycle stages.");
  }

  // ===== Test fakes =====

  private sealed class CapturingLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement payload, string messageTypeName) => payload;
  }

  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    private readonly List<(LifecycleStage Stage, IMessageEnvelope Envelope)> _invocations = [];
    private readonly Lock _lock = new();

    public List<(LifecycleStage Stage, IMessageEnvelope Envelope)> Invocations {
      get {
        lock (_lock) {
          return [.. _invocations];
        }
      }
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _invocations.Add((stage, envelope));
      }
      return ValueTask.CompletedTask;
    }
  }
}
