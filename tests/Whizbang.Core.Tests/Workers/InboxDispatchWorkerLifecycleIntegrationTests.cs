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
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Integration coverage for InboxDispatchWorker's lifecycle-firing contract.
///
/// <para>
/// Critical invariant: <strong>tag notifications must fire for every inbox event, regardless of
/// whether this service has a local perspective.</strong> Tag hooks subscribe to
/// <c>PostAllPerspectivesDetached</c> by default. Without these stages firing, frontend
/// subscribers wait forever on signals that never arrive.
/// </para>
///
/// <para>
/// a consumer 2026-05-03: BFF receives many cross-service events (saga events, draft job field
/// updates, etc.) that have <c>[NotificationTag]</c> attributes but no BFF-side perspective.
/// Those tags still need to push over SignalR — InboxDispatchWorker is the path that
/// fires PostAllPerspectives + PostLifecycle for such events. If this contract regresses,
/// the user's UI sits with empty fields because the per-field <c>draft-job-name</c>,
/// <c>draft-job-skills</c>, etc. tag notifications never reach the browser.
/// </para>
///
/// <para>
/// Companion to <see cref="TransportConsumerWorkerPostLifecycleTests"/> which covers the
/// live receive path. This file covers the inbox-orphan path.
/// </para>
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerLifecycleIntegrationTests {

  // ---------- shared test doubles ----------

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
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> Failures { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      Failures.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Records every (envelope, stage) pair the worker invokes, so tests can assert
  /// the lifecycle pipeline reaches the stages where tag hooks fire.</summary>
  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    public ConcurrentBag<(IMessageEnvelope Envelope, LifecycleStage Stage)> Invocations { get; } = [];
    public TaskCompletionSource<bool> PostAllPerspectivesInlineFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> PostAllPerspectivesDetachedFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> PostLifecycleInlineFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> PostLifecycleDetachedFired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Count-based completion signals. A test that expects N invocations of a stage awaits
    // WaitForCountAsync instead of polling a wall clock — under full-suite CPU contention a
    // wall-clock deadline can expire before the worker gets scheduled, producing a flake.
    private readonly System.Threading.Lock _gate = new();
    private readonly List<(LifecycleStage Stage, int Target, TaskCompletionSource Tcs)> _waiters = [];

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      List<TaskCompletionSource>? toRelease = null;
      lock (_gate) {
        Invocations.Add((envelope, stage));
        var count = Invocations.Count(i => i.Stage == stage);
        for (var i = _waiters.Count - 1; i >= 0; i--) {
          if (_waiters[i].Stage == stage && count >= _waiters[i].Target) {
            (toRelease ??= []).Add(_waiters[i].Tcs);
            _waiters.RemoveAt(i);
          }
        }
      }
      if (toRelease is not null) {
        foreach (var tcs in toRelease) { tcs.TrySetResult(); }
      }
      switch (stage) {
        case LifecycleStage.PostAllPerspectivesInline:
          PostAllPerspectivesInlineFired.TrySetResult(true);
          break;
        case LifecycleStage.PostAllPerspectivesDetached:
          PostAllPerspectivesDetachedFired.TrySetResult(true);
          break;
        case LifecycleStage.PostLifecycleInline:
          PostLifecycleInlineFired.TrySetResult(true);
          break;
        case LifecycleStage.PostLifecycleDetached:
          PostLifecycleDetachedFired.TrySetResult(true);
          break;
        default:
          break;
      }
      return ValueTask.CompletedTask;
    }

    /// <summary>Completes when the given stage has been invoked at least <paramref name="target"/>
    /// times. Returns an already-completed task if the count is already met.</summary>
    public Task WaitForCountAsync(LifecycleStage stage, int target) {
      lock (_gate) {
        if (Invocations.Count(i => i.Stage == stage) >= target) {
          return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((stage, target, tcs));
        return tcs.Task;
      }
    }

    public bool HasStage(LifecycleStage stage) => Invocations.Any(i => i.Stage == stage);
    public int CountStage(LifecycleStage stage) => Invocations.Count(i => i.Stage == stage);
  }

  /// <summary>Test deserializer that just returns the JsonElement as-is. The lifecycle
  /// path doesn't care about the concrete payload type for the no-perspective branch — it
  /// just needs to reconstruct an envelope. Returning the raw JsonElement is enough.</summary>
  private sealed class PassThroughLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName)
      => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] payload, string messageType)
      => JsonDocument.Parse(payload).RootElement;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName)
      => jsonElement;
  }

  /// <summary>Registry that reports a controllable list of perspectives. Tests pass
  /// either an empty list (every event is "no local perspective") or a list with the
  /// event type registered (perspective IS local — InboxDispatchWorker should defer
  /// PostAllPerspectives to PerspectiveWorker).</summary>
  private sealed class StubPerspectiveRunnerRegistry(
      IReadOnlyList<PerspectiveRegistrationInfo> perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private static InboxWork _makeWork(string messageType) {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = messageType,
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private static (
      InboxDispatchWorker worker,
      FakeInboxChannelWriter inbox,
      FakeHandlerCommitChannel handlerCommit,
      CapturingReceptorInvoker invoker)
    _buildWorker(IReadOnlyList<PerspectiveRegistrationInfo>? registeredPerspectives) {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var invoker = new CapturingReceptorInvoker();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    if (registeredPerspectives is not null) {
      services.AddScoped<IPerspectiveRunnerRegistry>(_ => new StubPerspectiveRunnerRegistry(registeredPerspectives));
    }
    var sp = services.BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer());

    return (worker, inbox, handlerCommit, invoker);
  }

  // ---------- TESTS ----------

  [Test]
  public async Task NoLocalPerspective_FiresPostAllPerspectivesInline_SoTagHooksCanEmitAsync() {
    // Regression lock: when an inbox event arrives that no local perspective handles —
    // e.g., a SagaItem event arriving in BFF that only updates JobService's projections —
    // InboxDispatchWorker MUST fire PostAllPerspectivesInline so [NotificationTag] /
    // [NotificationIdTag] hooks (registered at PostAllPerspectivesDetached by default in a consumer)
    // can push the SignalR notification. Without this, downstream UI subscribers (per-field
    // load actions tied to `draft-job-name`, `draft-job-skills`, etc.) never receive a signal
    // and the canvas sits with empty data even though the projections in OTHER services
    // have committed. This was the a consumer 2026-05-03 saga-loader-stuck symptom.
    var (worker, inbox, _, invoker) = _buildWorker(registeredPerspectives: []);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork("Some.Cross.Service.Event, Some.Contracts");
    await inbox.WriteAsync(work, cts.Token);

    // Wait for BOTH inline and detached PostAllPerspectives TCSs — detached schedules on a
    // background thread and may not have run by the time inline completes, so awaiting only
    // the inline signal races with the detached assertion (per feedback_no_timing_tests).
    await Task.WhenAll(
      invoker.PostAllPerspectivesInlineFired.Task.WaitAsync(TimeSpan.FromSeconds(5)),
      invoker.PostAllPerspectivesDetachedFired.Task.WaitAsync(TimeSpan.FromSeconds(5)));

    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because("Inbox events with no local perspective MUST still fire PostAllPerspectivesInline so tag hooks can emit notifications.");
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesDetached)).IsTrue()
      .Because("PostAllPerspectivesDetached must also fire — a consumer's ConsumerNotificationTagHook is registered at this stage.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task NoLocalPerspective_FiresPostLifecycleInline_AsFinalStageAsync() {
    // Companion lock: PostLifecycle is the framework's "all done with this event" marker.
    // Tests / observability subscribers may bind to it. Must fire alongside
    // PostAllPerspectives for no-perspective inbox events.
    var (worker, inbox, _, invoker) = _buildWorker(registeredPerspectives: []);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork("Some.Cross.Service.Event, Some.Contracts");
    await inbox.WriteAsync(work, cts.Token);

    // Wait for BOTH Inline and Detached to fire — Detached is fire-and-forget on a
    // separate scope, so it may not have completed by the time Inline finishes.
    await Task.WhenAll(
      invoker.PostLifecycleInlineFired.Task.WaitAsync(TimeSpan.FromSeconds(5)),
      invoker.PostLifecycleDetachedFired.Task.WaitAsync(TimeSpan.FromSeconds(5)));

    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleDetached)).IsTrue();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task NoLocalPerspective_PostAllPerspectives_FiresExactlyOnce_PerInlineAndDetachedAsync() {
    // Multiple events on the same inbox — each must get exactly one Pre/Post pair per stage.
    // Catches accidental double-fire (e.g., from a stage-handling refactor that loops twice).
    var (worker, inbox, _, invoker) = _buildWorker(registeredPerspectives: []);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work1 = _makeWork("Cross.A, Some");
    var work2 = _makeWork("Cross.B, Some");
    await inbox.WriteAsync(work1, cts.Token);
    await inbox.WriteAsync(work2, cts.Token);

    // Await both events' PostAllPerspectivesInline via a count-based completion signal.
    // The invoker resolves WaitForCountAsync the instant the second invocation lands, so
    // there is no wall-clock dependency — the previous DateTimeOffset deadline could expire
    // under full-suite CPU contention before the worker was scheduled, causing a flake.
    // The 5s ceiling here is a hang-guard, not the success path.
    await invoker.WaitForCountAsync(LifecycleStage.PostAllPerspectivesInline, 2)
      .WaitAsync(TimeSpan.FromSeconds(5));
    await invoker.WaitForCountAsync(LifecycleStage.PostLifecycleInline, 2)
      .WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(invoker.CountStage(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(2)
      .Because("PostAllPerspectivesInline fires exactly once per event in the no-perspective path.");
    await Assert.That(invoker.CountStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(2);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task LocalPerspectiveExistsForEvent_DoesNotFirePostAllPerspectives_DeferredToPerspectiveWorkerAsync() {
    // Inverse contract: when this service HAS a perspective for the event,
    // InboxDispatchWorker must NOT fire PostAllPerspectives — that's PerspectiveWorker's
    // job after the perspective applies. Firing here would double-fire (and worse, fire
    // BEFORE the perspective applies, racing against the read model).
    const string handledType = "Handled.Local.Event, Test";
    var (worker, inbox, _, invoker) = _buildWorker(registeredPerspectives: [
      new PerspectiveRegistrationInfo(
        ClrTypeName: "Test.LocalPerspective",
        FullyQualifiedName: "global::Test.LocalPerspective",
        ModelType: "global::Test.Model",
        EventTypes: [handledType])
    ]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork(handledType);
    await inbox.WriteAsync(work, cts.Token);

    // Wait for PostInbox stages — those fire regardless. Then assert PostAllPerspectives
    // does NOT fire from InboxDispatchWorker.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (!invoker.HasStage(LifecycleStage.PostInboxInline) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    // Give a tick for any erroneous downstream stages to fire — bounded by yields
    for (var i = 0; i < 50; i++) {
      await Task.Yield();
    }

    await Assert.That(invoker.HasStage(LifecycleStage.PostInboxInline)).IsTrue()
      .Because("PostInbox always fires, regardless of perspective registration.");
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsFalse()
      .Because("Events with a local perspective: PostAllPerspectives is deferred to PerspectiveWorker (which fires it after the perspective applies). InboxDispatchWorker must not fire it here, or the tag would race ahead of the read-model commit.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task NoPerspectiveRegistry_AllEventsTreatedAsNoPerspective_FiresPostAllPerspectivesAsync() {
    // Edge case: when no IPerspectiveRunnerRegistry is registered at all (e.g., minimal
    // test harness, services that consume but never project), every inbox event is
    // implicitly "no perspective" and must fire the full lifecycle through PostLifecycle.
    var (worker, inbox, _, invoker) = _buildWorker(registeredPerspectives: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork("Anything.Goes, Test");
    await inbox.WriteAsync(work, cts.Token);

    await invoker.PostAllPerspectivesInlineFired.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
