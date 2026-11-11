// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>The outbox publish worker begins a message's lifecycle tracking in its pre-outbox stages and ended it only
/// after the post-outbox stages (#2228). A publish that threw, in the singular or the bulk path, left the entry (with
/// the typed envelope and payload) in the singleton coordinator until the stale sweep. The worker now ends it on every
/// outcome, before the failure is routed, so the failure channel's signal is the point to assert at.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxPublishWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public sealed class OutboxPublishWorkerTrackingEndsTests {
  private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

  [Test]
  public async Task SingularPublishThrows_EndsTheMessagesTracking_BeforeRoutingTheFailureAsync() {
    using var harness = new Harness(new ThrowingStrategy(bulk: false), bulk: false);
    var work = _work();

    var routed = await harness.RunUntilFailureAsync(work);

    await Assert.That(routed.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(harness.TrackedCountAtFailure).IsEqualTo(0)
      .Because("the worker began the entry in its pre-outbox stages, so it ends it when the publish throws");
  }

  [Test]
  public async Task BulkPublishThrows_EndsEveryMessagesTracking_BeforeRoutingTheFailuresAsync() {
    using var harness = new Harness(new ThrowingStrategy(bulk: true), bulk: true);
    var work = _work();

    var routed = await harness.RunUntilFailureAsync(work);

    await Assert.That(routed.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(harness.TrackedCountAtFailure).IsEqualTo(0)
      .Because("each item's pre-outbox stages began an entry, so a failed batch ends every one of them");
  }

  private static OutboxWork _work() {
    Guid msgId = TrackedGuid.New();
    return new OutboxWork {
      MessageId = msgId,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed class Harness : IDisposable {
    private readonly LifecycleCoordinator _coordinator = new(NullLogger<LifecycleCoordinator>.Instance);
    private readonly WorkChannel _channel = new();
    private readonly FailureChannel _failure;
    private readonly OutboxPublishWorker _worker;

    public Harness(IMessagePublishStrategy strategy, bool bulk) {
      _failure = new FailureChannel(_coordinator);
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<ILifecycleCoordinator>(_coordinator);
      services.AddScoped<IReceptorInvoker, CompletingInvoker>();
      var provider = services.BuildServiceProvider();
      _worker = new OutboxPublishWorker(
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        workChannelWriter: _channel,
        outboxCompletionChannel: new CompletionChannel(),
        failureChannel: _failure,
        options: Options.Create(new OutboxPublishWorkerOptions { Enabled = true, MaxBulkPublishBatchSize = bulk ? 10 : 1 }),
        logger: NullLogger<OutboxPublishWorker>.Instance,
        instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
        publishStrategy: strategy,
        lifecycleMessageDeserializer: new PlaceholderDeserializer(),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        pinnedPool: NoOpPinnedConnectionPool.Instance,
        occurrenceGate: new NoOpOccurrencePublishGate());
    }

    public int TrackedCountAtFailure => _failure.TrackedCountAtFirstFailure;

    public void Dispose() => _worker.Dispose();

    public async Task<MessageFailure> RunUntilFailureAsync(OutboxWork work) {
      using var timeout = new CancellationTokenSource(_testTimeout);
      await _worker.StartAsync(CancellationToken.None);
      try {
        await _channel.WriteAsync(work, timeout.Token);
        return await _failure.FirstFailure.Task.WaitAsync(timeout.Token);
      } finally {
        await _worker.StopAsync(CancellationToken.None);
      }
    }
  }

  private sealed class ThrowingStrategy(bool bulk) : IMessagePublishStrategy {
    public bool SupportsBulkPublish => bulk;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("publish exploded");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("batch publish exploded");
  }

  /// <summary>Reads the coordinator's tracked count at the moment the first failure is routed.</summary>
  private sealed class FailureChannel(LifecycleCoordinator coordinator) : IFailureChannel {
    public TaskCompletionSource<MessageFailure> FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TrackedCountAtFirstFailure { get; private set; } = -1;
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      if (!FirstFailure.Task.IsCompleted) {
        TrackedCountAtFirstFailure = coordinator.TrackedCount;
      }
      FirstFailure.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class CompletingInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class PlaceholderDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => new();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => new();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => new();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => new();
  }

  private sealed class WorkChannel : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
#pragma warning disable CS0067 // never raised: the worker under test only subscribes
    public event Action? OnNewWorkAvailable;
    public event Action? OnNewPerspectiveWorkAvailable;
#pragma warning restore CS0067
    public void SignalNewWorkAvailable() { }
    public void SignalNewPerspectiveWorkAvailable() { }
  }
}
