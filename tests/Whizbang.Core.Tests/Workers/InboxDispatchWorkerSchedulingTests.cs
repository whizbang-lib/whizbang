using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the per-message dispatch cost contract for InboxDispatchWorker after the
/// 2026-06 BFF slowness investigation: detached lifecycle bodies must use the
/// ThreadPool (Task.Run) — not <see cref="TaskCreationOptions.LongRunning"/> — and
/// must NOT re-establish security context per detached scope (AsyncLocal flows from
/// the outer dispatch scope via ExecutionContext into Task.Run continuations).
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerSchedulingTests {

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
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

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class AllStagesReceptorRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private sealed class PassthroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] payload, string messageType) => JsonDocument.Parse(payload).RootElement;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }

  private sealed class CountingSecurityProvider : IMessageSecurityContextProvider {
    private int _count;
    public int CallCount => Volatile.Read(ref _count);
    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken ct = default) {
      Interlocked.Increment(ref _count);
      return ValueTask.FromResult<IScopeContext?>(null);
    }
  }

  /// <summary>
  /// Records IsThreadPoolThread on PostInboxDetached invocation; TCSes signal stage
  /// firing so tests stay deterministic (no Task.Delay).
  /// </summary>
  private sealed class SchedulingObservingInvoker : IReceptorInvoker {
    public TaskCompletionSource<bool> PostDetachedOnPoolThread { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PreDetachedFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PostDetachedFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      switch (stage) {
        case LifecycleStage.PostInboxDetached:
          PostDetachedOnPoolThread.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
          PostDetachedFired.TrySetResult();
          break;
        case LifecycleStage.PreInboxDetached:
          PreDetachedFired.TrySetResult();
          break;
      }
      return ValueTask.CompletedTask;
    }
  }

  private static InboxWork _makeWork() {
    var messageId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "Whizbang.Core.Tests.SchedulingFixture.Event, TestAsm",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private static InboxDispatchWorker _buildWorker(
      ServiceProvider sp,
      FakeInstanceProvider instance,
      FakeInboxChannelWriter inbox,
      FakeHandlerCommitChannel handlerCommit,
      FakeFailureChannel failure,
      SchemaReadyGate gate) =>
    new(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new PassthroughDeserializer(),
      receptorRegistry: new AllStagesReceptorRegistry());

  [Test]
  public async Task PostInboxDetached_RunsOnThreadPoolThread_NotLongRunningAsync() {
    // PostInboxDetached must run on a pooled ThreadPool worker (Task.Run).
    // TaskCreationOptions.LongRunning was originally added for CI starvation but spawns
    // a dedicated OS thread per inbox message in production — on the JDX BFF this
    // produced ~10 msg/min throughput under a 36k inbox backlog (kernel scheduler
    // thrash on per-message pthread_create). Confirmed by reading
    // BackgroundStageDispatch.StartLongRunning + InboxDispatchWorker.cs:317.
    var invoker = new SchedulingObservingInvoker();
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    await using var sp = services.BuildServiceProvider();
    var worker = _buildWorker(sp, instance, inbox, handlerCommit, failure, gate);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_makeWork(), cts.Token);

    var wasPoolThread = await invoker.PostDetachedOnPoolThread.Task.WaitAsync(cts.Token);

    await Assert.That(wasPoolThread).IsTrue()
      .Because("PostInboxDetached must run on a pooled ThreadPool worker. " +
               "TaskCreationOptions.LongRunning spawns a dedicated OS thread per message, " +
               "dominating per-message dispatch cost on busy services.");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  [Test]
  public async Task EstablishContextAsync_CalledOncePerInboxMessage_NotPerDetachedScopeAsync() {
    // Outer dispatch scope's EstablishFullContextAsync sets static-AsyncLocal-backed
    // accessors (ScopeContextAccessor + MessageContextAccessor). ExecutionContext flows
    // those values into Task.Run continuations by default, so the detached body's fresh
    // DI scope inherits the security state without re-establishing.
    //
    // Pre-fix: 3 calls (outer + PreInbox detached + PostInbox detached).
    // Post-fix: 1 call (outer only).
    var counter = new CountingSecurityProvider();
    var invoker = new SchedulingObservingInvoker();
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddSingleton<IMessageSecurityContextProvider>(counter);
    await using var sp = services.BuildServiceProvider();
    var worker = _buildWorker(sp, instance, inbox, handlerCommit, failure, gate);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_makeWork(), cts.Token);

    // EstablishContextAsync runs BEFORE the receptor invocation inside the detached
    // body (SecurityContextHelper.cs:148 → InboxDispatchWorker.cs:454 → :460). When
    // BOTH detached receptors have fired, every EstablishContextAsync call against
    // this message's scopes has already completed.
    await Task.WhenAll(
      invoker.PreDetachedFired.Task.WaitAsync(cts.Token),
      invoker.PostDetachedFired.Task.WaitAsync(cts.Token));

    await Assert.That(counter.CallCount).IsEqualTo(1)
      .Because("Outer dispatch scope's EstablishFullContextAsync sets static AsyncLocal " +
               "values that flow into the detached ExecutionContext. Detached bodies must " +
               "NOT re-establish — that work is pure overhead and (in production) competes " +
               "for DB connections with the actual dispatch path.");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }
}
