using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Issue #485: <c>PostDistributeInline</c> delivery was contingent on WHICH flush trigger fired —
/// the caller's manual flush ran the lifecycle stages, but the BatchSize and Debounce triggers set
/// <c>SkipLifecycle: true</c>, so under volume a receptor invocation silently never happened. The
/// skip's stated reason ("background thread, no ambient context") is stale:
/// <see cref="LifecycleInvocationHelper"/> is self-contained — it reconstructs each message from
/// its envelope (trace context from hops, payload deserialized, fresh scope per message) and never
/// reads ambient state. Delivery must therefore be trigger-independent. The one legitimate
/// exception is the DISPOSAL flush — a shutdown path where backgrounded stage halves would race
/// process exit — which stays skipped, and is pinned here as deliberate.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/BatchWorkCoordinatorStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntervalWorkCoordinatorStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorFlushHelper.cs</code-under-test>
public class LifecycleStageTriggerIndependenceTests {

  private sealed class RecordingReceptorInvoker : IReceptorInvoker {
    private readonly List<LifecycleStage> _stages = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _inlineSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task InlineSeen => _inlineSeen.Task;
    public IReadOnlyList<LifecycleStage> Stages {
      get {
        lock (_lock) {
          return [.. _stages];
        }
      }
    }
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _stages.Add(stage);
      }
      if (stage == LifecycleStage.PostDistributeInline) {
        _inlineSeen.TrySetResult();
      }
      return default;
    }
  }

  private sealed class PassthroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => new();
    public object DeserializeFromBytes(byte[] payload, string messageTypeName) => new();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => new();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => new();
  }

  private sealed class SilentCoordinator : IWorkCoordinator {
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => throw new NotSupportedException("not exercised by this test");
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("not exercised by this test");
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "lifecycle-trigger-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static OutboxMessage _outboxMessage() {
    var id = Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
    };
  }

  private static (BatchWorkCoordinatorStrategy Strategy, RecordingReceptorInvoker Invoker) _build(
      int batchSize, int debounceMs) {
    var invoker = new RecordingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    var provider = services.BuildServiceProvider();
    var strategy = new BatchWorkCoordinatorStrategy(
      new SilentCoordinator(),
      new Pod(),
      new WorkCoordinatorOptions {
        Strategy = WorkCoordinatorStrategy.Batch,
        BatchSize = batchSize,
        IntervalMilliseconds = debounceMs,
        PartitionCount = 4,
        LeaseSeconds = 300,
        AbandonStaleInstanceThresholdSeconds = 300,
      },
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new PassthroughDeserializer());
    return (strategy, invoker);
  }

  [Test]
  [Timeout(30000)]
  public async Task BatchSizeTrigger_StillInvokesPostDistributeInlineAsync(CancellationToken cancellationToken) {
    var (strategy, invoker) = _build(batchSize: 2, debounceMs: 60000);
    try {
      strategy.QueueOutboxMessage(_outboxMessage());
      strategy.QueueOutboxMessage(_outboxMessage());   // volume trigger fires here

      await invoker.InlineSeen.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(invoker.Stages).Contains(LifecycleStage.PostDistributeInline)
        .Because("issue #485: delivery must not depend on WHICH trigger flushed — under volume the "
               + "batch trigger wins most flushes, and every skipped invocation is a silent drop");
    } finally {
      await strategy.DisposeAsync();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task DebounceTrigger_StillInvokesPostDistributeInlineAsync(CancellationToken cancellationToken) {
    var (strategy, invoker) = _build(batchSize: 100, debounceMs: 50);
    try {
      strategy.QueueOutboxMessage(_outboxMessage());   // below batch size; debounce timer flushes

      await invoker.InlineSeen.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(invoker.Stages).Contains(LifecycleStage.PostDistributeInline)
        .Because("a quiet-period flush is the common case on low-traffic services — skipping there "
               + "makes stage delivery a function of traffic shape");
    } finally {
      await strategy.DisposeAsync();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task ManualFlush_InvokesPostDistributeInline_UnchangedAsync(CancellationToken cancellationToken) {
    var (strategy, invoker) = _build(batchSize: 100, debounceMs: 60000);
    try {
      strategy.QueueOutboxMessage(_outboxMessage());
      _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None, CancellationToken.None);

      await invoker.InlineSeen.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(invoker.Stages).Contains(LifecycleStage.PostDistributeInline);
    } finally {
      await strategy.DisposeAsync();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task DisposalFlush_StillSkipsLifecycle_TheOneDeliberateExceptionAsync(CancellationToken cancellationToken) {
    var (strategy, invoker) = _build(batchSize: 100, debounceMs: 60000);
    strategy.QueueOutboxMessage(_outboxMessage());

    await strategy.DisposeAsync();   // shutdown drain — the message is stored, stages are not run

    await Assert.That(invoker.Stages).DoesNotContain(LifecycleStage.PostDistributeInline)
      .Because("disposal is a shutdown path: backgrounded stage halves would race process exit, "
             + "so the drain stores durably and deliberately skips the stages");
  }
}
