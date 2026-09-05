using System.Collections.Concurrent;
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
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 4 of plans/pump-then-process.md — InboxDispatchWorker gates lifecycle deserialize
/// by <see cref="IReceptorRegistryQuery"/>. Without the gate the worker tries to deserialize
/// every payload before discovering there's no receptor, which is wasted work for cross-
/// service event types a consumer's service receives where most have no local handler.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerLifecycleGatingTests {

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
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      All.Add(request);
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default)
      => ValueTask.CompletedTask;
  }

  /// <summary>Records every invocation so tests can assert which lifecycle stages fired.</summary>
  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    public ConcurrentBag<LifecycleStage> Invocations { get; } = [];
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Invocations.Add(stage);
      return ValueTask.CompletedTask;
    }
    public bool HasStage(LifecycleStage stage) => Invocations.Any(s => s == stage);
  }

  /// <summary>Counts deserialize calls so tests can prove the gate skipped the payload work.</summary>
  private sealed class CountingLifecycleDeserializer : ILifecycleMessageDeserializer {
    public int CallCount;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      Interlocked.Increment(ref CallCount);
      return envelope.Payload;
    }
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      Interlocked.Increment(ref CallCount);
      return envelope.Payload;
    }
    public object DeserializeFromBytes(byte[] payload, string messageType) {
      Interlocked.Increment(ref CallCount);
      return JsonDocument.Parse(payload).RootElement;
    }
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      Interlocked.Increment(ref CallCount);
      return jsonElement;
    }
  }

  /// <summary>Test-controllable registry query.</summary>
  private sealed class FakeReceptorRegistry(
      Func<LifecycleStage, string, bool>? hasReceptors = null,
      Func<string, bool>? hasInboxHandler = null,
      Func<string, bool>? hasAnyConsumer = null) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType)
      => hasReceptors?.Invoke(stage, messageType) ?? false;
    public bool HasInboxHandler(string messageType)
      => hasInboxHandler?.Invoke(messageType) ?? false;
    public bool HasAnyConsumer(string messageType)
      => hasAnyConsumer?.Invoke(messageType) ?? false;
  }

  /// <summary>
  /// Runtime-registry double. Only <see cref="GetReceptorsFor"/> is exercised by the
  /// dispatch gate — the Register/Unregister overloads throw if a test reaches them,
  /// since none of the lifecycle-gating tests should be touching runtime registration
  /// from the registry side.
  /// </summary>
  private sealed class FakeRuntimeReceptorRegistry(
      Func<Type, LifecycleStage, IReadOnlyList<ReceptorInfo>>? getReceptorsFor = null) : IReceptorRegistry {
    private static readonly IReadOnlyList<ReceptorInfo> _empty = [];
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage)
      => getReceptorsFor?.Invoke(messageType, stage) ?? _empty;
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage
      => throw new NotSupportedException("Test double — register receptors via the GetReceptorsFor delegate.");
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage
      => throw new NotSupportedException("Test double — register receptors via the GetReceptorsFor delegate.");
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
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

  /// <summary>
  /// Owns the worker + the ServiceProvider feeding its scope factory. Tests dispose
  /// this so detached lifecycle tasks that capture <c>_scopeFactory</c> don't keep
  /// fire-and-forget scopes alive past the test boundary — that's what was causing
  /// adjacent <c>InboxDispatchWorkerLifecycleIntegrationTests</c> tests to flake on
  /// their detached-stage TaskCompletionSource within the
  /// <c>WhizbangBackgroundServiceTests</c> non-parallel group when the ThreadPool was
  /// congested by leaked scopes.
  /// </summary>
  private sealed class WorkerHarness(
      InboxDispatchWorker worker,
      ServiceProvider sp,
      FakeInboxChannelWriter inbox,
      FakeHandlerCommitChannel handlerCommit,
      CapturingReceptorInvoker invoker,
      CountingLifecycleDeserializer deserializer) : IAsyncDisposable {
    public InboxDispatchWorker Worker { get; } = worker;
    public FakeInboxChannelWriter Inbox { get; } = inbox;
    public FakeHandlerCommitChannel HandlerCommit { get; } = handlerCommit;
    public CapturingReceptorInvoker Invoker { get; } = invoker;
    public CountingLifecycleDeserializer Deserializer { get; } = deserializer;
    public async ValueTask DisposeAsync() {
      try {
        await Worker.StopAsync(CancellationToken.None);
      } catch {
        // best-effort; tests already canceled the stoppingToken before disposal
      }
      await sp.DisposeAsync();
    }
  }

  private static WorkerHarness _buildWorker(IReceptorRegistryQuery? registry, IReceptorRegistry? runtimeRegistry = null) {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var invoker = new CapturingReceptorInvoker();
    var deserializer = new CountingLifecycleDeserializer();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: deserializer,
      receptorRegistry: registry,
      runtimeReceptorRegistry: runtimeRegistry);

    return new WorkerHarness(worker, sp, inbox, handlerCommit, invoker, deserializer);
  }

  // ---------- TESTS ----------

  [Test]
  public async Task Process_NoLifecycleReceptors_SkipsPreAndPostInboxOnlyAsync() {
    // Registry returns false for every Pre/Post Inbox query. The gate must skip deserialize
    // for THOSE stages — saves wasted JSON parsing on cross-service events the local service
    // doesn't subscribe to. PostAllPerspectives + PostLifecycle are NOT gated by the
    // registry (the static WhizbangReceptorRegistryQuery doesn't emit entries for those
    // stages, so a generic gate would silently break them); they still fire when the
    // worker observes no local perspectives. See the
    // _RegistryReturnsFalseForUnknownStages_PostAllPerspectivesStillFires regression test
    // below for the failure mode this guards against.
    var registry = new FakeReceptorRegistry(hasReceptors: (_, _) => false);
    await using var harness = _buildWorker(registry);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Cross.Service.Event, Some.Contracts");
    await harness.Inbox.WriteAsync(work, cts.Token);

    // Wait for commit to fire — proves _processOneAsync ran to completion.
    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxInline)).IsFalse()
      .Because("PreInboxInline must not fire when registry reports no receptors for that gated stage.");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxInline)).IsFalse()
      .Because("PostInboxInline must not fire when registry reports no receptors for that gated stage.");
  }

  [Test]
  public async Task Process_OnlyPreInboxRegistered_FiresPreInboxAndSkipsPostInboxAsync() {
    // PreInbox has a receptor; PostInbox does not. Worker must fire PreInbox lifecycle
    // (deserialize + invoke) but skip PostInbox entirely.
    var registry = new FakeReceptorRegistry(hasReceptors: (stage, _) =>
      stage == LifecycleStage.PreInboxDetached || stage == LifecycleStage.PreInboxInline);
    await using var harness = _buildWorker(registry);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Pre.Only.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (!harness.Invoker.HasStage(LifecycleStage.PreInboxInline) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }
    for (var i = 0; i < 50; i++) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxInline)).IsTrue()
      .Because("PreInboxInline must fire when its registry entry is populated.");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxInline)).IsFalse()
      .Because("PostInboxInline must NOT fire when the registry reports no PostInbox receptors.");

    await cts.CancelAsync();
  }

  [Test]
  public async Task Process_OnlyPostInboxRegistered_FiresPostInboxAndSkipsPreInboxAsync() {
    // Inverse: PostInbox is registered, PreInbox is not.
    var registry = new FakeReceptorRegistry(hasReceptors: (stage, _) =>
      stage == LifecycleStage.PostInboxDetached || stage == LifecycleStage.PostInboxInline);
    await using var harness = _buildWorker(registry);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Post.Only.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (!harness.Invoker.HasStage(LifecycleStage.PostInboxInline) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }
    for (var i = 0; i < 50; i++) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxInline)).IsTrue();
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxInline)).IsFalse();

    await cts.CancelAsync();
  }

  [Test]
  public async Task Process_BothRegistered_FiresBothAsync() {
    // Happy path lock — both stages registered → both fire. Catches a regression where the
    // gate logic accidentally short-circuits when only one of (Detached|Inline) returns true.
    var registry = new FakeReceptorRegistry(hasReceptors: (_, _) => true);
    await using var harness = _buildWorker(registry);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Both.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while ((!harness.Invoker.HasStage(LifecycleStage.PreInboxInline) || !harness.Invoker.HasStage(LifecycleStage.PostInboxInline))
           && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxInline)).IsTrue();
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxInline)).IsTrue();

    await cts.CancelAsync();
  }

  [Test]
  public async Task Process_RegistryReturnsFalseForUnknownStages_PostAllPerspectivesStillFiresAsync() {
    // Critical regression lock: the source-generated WhizbangReceptorRegistryQuery only
    // emits Pre/Post Inbox stages — PostAllPerspectives and PostLifecycle return false from
    // HasReceptors. A generic "gate when both detached + inline are false" would silently
    // skip those stages in production (registry IS injected by AddWhizbangWorkers).
    // Cross-service events with no local perspective would lose tag notifications because
    // PostAllPerspectivesDetached / a consumer's notification tag hook would never fire.
    //
    // The gate must scope itself to stages the registry actually knows about. This test
    // injects a registry that returns false for every query (mimicking the unknown-stage
    // behavior of the static class) and asserts PostAllPerspectives still fires when there
    // are no local perspectives — proving the gate scopes correctly.
    var registry = new FakeReceptorRegistry(hasReceptors: (_, _) => false);
    await using var harness = _buildWorker(registry);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Cross.Service.Event.NoLocalPerspective, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (!harness.Invoker.HasStage(LifecycleStage.PostAllPerspectivesInline) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because("PostAllPerspectives MUST fire even when the registry reports false for it — the registry doesn't track that stage. A generic gate would silently break tag notifications for cross-service events.");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue()
      .Because("PostLifecycle MUST fire for the same reason.");

    await cts.CancelAsync();
  }

  // ========================================================================
  // SLICE 15 + 16 — deserialize-once + skip empty-detached spawn invariants
  // ========================================================================

  private static WorkerHarness _buildWorkerWithCache(IReceptorRegistryQuery? registry, InboxDeserializeCache? cache) {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var invoker = new CapturingReceptorInvoker();
    var deserializer = new CountingLifecycleDeserializer();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: deserializer,
      receptorRegistry: registry,
      deserializeCache: cache);

    return new WorkerHarness(worker, sp, inbox, handlerCommit, invoker, deserializer);
  }

  [Test]
  public async Task Slice15_DeserializeOnce_AcrossAllFourLifecycleStagesAsync() {
    // Lock the slice 15 invariant: a single inbox message dispatches through up to four
    // lifecycle stages (PreInbox / PostInbox / PostAllPerspectives / PostLifecycle) but the
    // payload MUST deserialize exactly once. Pre-slice-15 the deserializer was called inside
    // each stage helper — 4× wasted JSON parsing per message at saga fan-out load.
    var registry = new FakeReceptorRegistry(hasReceptors: (_, _) => true);
    await using var harness = _buildWorkerWithCache(registry, cache: null);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);
    var work = _makeWork("Slice15.AllStages.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while ((!harness.Invoker.HasStage(LifecycleStage.PreInboxInline)
            || !harness.Invoker.HasStage(LifecycleStage.PostInboxInline)
            || !harness.Invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)
            || !harness.Invoker.HasStage(LifecycleStage.PostLifecycleInline))
           && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Deserializer.CallCount).IsEqualTo(1)
      .Because("Slice 15 invariant: payload must deserialize exactly once per dispatch even when all 4 lifecycle stages fire.");
    await cts.CancelAsync();
  }

  [Test]
  public async Task Slice15_CacheHit_OnRedeliveryWithinTtl_SkipsDeserializeAsync() {
    // Lock the slice 15 cache invariant: when the same messageId is re-dispatched (transport
    // redelivery, lease re-claim, retry) within the cache's TTL window, the deserializer is
    // NOT called again. Pre-slice-15 every redelivery re-parsed the JSON.
    var registry = new FakeReceptorRegistry(hasReceptors: (_, _) => true);
    var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
    var cache = new InboxDeserializeCache(new SystemTimeProvider(fake), ttl: TimeSpan.FromMinutes(2));
    await using var harness = _buildWorkerWithCache(registry, cache);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    // First dispatch — populates the cache.
    var work = _makeWork("Slice15.Cache.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);
    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var firstCount = harness.Deserializer.CallCount;

    // Redeliver the SAME messageId (transport double-fire scenario). New work record but
    // shares the message id so the cache key collides.
    var redelivery = work with { Attempts = 1 };
    await harness.Inbox.WriteAsync(redelivery, cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (harness.HandlerCommit.All.Count < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.HandlerCommit.All.Count).IsEqualTo(2)
      .Because("Both dispatches should reach completion.");
    await Assert.That(harness.Deserializer.CallCount).IsEqualTo(firstCount)
      .Because("Cache hit on redelivery within TTL must skip the deserialize call entirely.");
    await cts.CancelAsync();
  }

  [Test]
  public async Task Slice16_NoDetachedReceptor_DoesNotSpawnDetachedScopeAsync() {
    // Lock the slice 16 invariant: when only the inline form of a stage has receptors, the
    // detached fire-and-forget Task.Run + new DI scope + security context establishment is
    // skipped. Pre-slice-16 every gated stage spawned a detached task even if no receptor
    // would ever fire — pure overhead at saga fan-out load.
    //
    // We assert this indirectly: the CapturingReceptorInvoker records every call. If the
    // detached path spawned, it would record a *Detached stage in addition to *Inline.
    var registry = new FakeReceptorRegistry(hasReceptors: (stage, _) =>
      stage == LifecycleStage.PreInboxInline || stage == LifecycleStage.PostInboxInline);
    await using var harness = _buildWorkerWithCache(registry, cache: null);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Slice16.InlineOnly.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Give detached tasks a chance to spawn if they're going to.
    for (var i = 0; i < 100; i++) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxInline)).IsTrue()
      .Because("Inline stage with a registered receptor must still fire.");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxDetached)).IsFalse()
      .Because("Slice 16: detached stage with no registered receptor must NOT spawn a Task.Run / new scope.");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxDetached)).IsFalse()
      .Because("Same invariant for PostInbox detached stage.");
    await cts.CancelAsync();
  }

  [Test]
  public async Task Slice16_DetachedReceptorRegistered_StillSpawnsDetachedAsync() {
    // Positive complement to the slice 16 lock: when a receptor IS registered for the detached
    // stage, the spawn still happens. Catches a regression where the new gate becomes too
    // aggressive and skips legitimate detached work.
    var registry = new FakeReceptorRegistry(hasReceptors: (_, _) => true);
    await using var harness = _buildWorkerWithCache(registry, cache: null);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Slice16.BothRegistered.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while ((!harness.Invoker.HasStage(LifecycleStage.PreInboxDetached)
            || !harness.Invoker.HasStage(LifecycleStage.PostInboxDetached))
           && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxDetached)).IsTrue()
      .Because("With a registered detached receptor, the spawn must still happen.");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxDetached)).IsTrue();
    await cts.CancelAsync();
  }

  [Test]
  public async Task Process_RuntimeReceptorRegistered_ButCompileTimeEmpty_FiresLifecycleAsync() {
    // Regression lock for the integration-test failure where PreInboxDetached on
    // a consumer's service never fired for ProductCreatedEvent: the service's source-generated
    // compile-time WhizbangReceptorRegistryQuery contribution emits EMPTY arrays for
    // PreInboxDetached / PreInboxInline (that service has no compile-time lifecycle receptors —
    // only a perspective consumer). The dispatch worker's HasReceptors gate consulted
    // ONLY that compile-time view, so it returned early and the runtime-registered
    // test completion receptor never fired. Wait helper timed out.
    //
    // IReceptorRegistry.GetReceptorsFor's contract is explicit: "Returns compile-time
    // entries concatenated with any runtime-registered entries." The gate MUST consult
    // the runtime registry too, or every runtime registration silently breaks. This is
    // the same class of bug as [[feedback_fast_return_must_signal_lifecycle]] —
    // an optimization that skips firing a lifecycle stage.
    var compileTime = new FakeReceptorRegistry(hasReceptors: (_, _) => false);
    var runtime = new FakeRuntimeReceptorRegistry(getReceptorsFor: (type, stage) =>
      stage == LifecycleStage.PreInboxDetached
        ? [new ReceptorInfo(
            MessageType: type,
            ReceptorId: "test-runtime-receptor",
            InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null))]
        : Array.Empty<ReceptorInfo>());
    await using var harness = _buildWorker(compileTime, runtime);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("Runtime.Only.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (!harness.Invoker.HasStage(LifecycleStage.PreInboxDetached) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxDetached)).IsTrue()
      .Because("Gate must consult the runtime registry too — without this, every runtime-registered receptor (integration test waits, dynamic registrations) silently fails to fire.");

    await cts.CancelAsync();
  }

  [Test]
  public async Task Process_NullRegistry_PreservesLegacyBehaviorFiresLifecycleAsync() {
    // Backward compatibility: when no IReceptorRegistryQuery is provided (test harnesses
    // and services without the source generator wired), the worker falls back to the
    // pre-slice-4 behavior — fire lifecycle stages unconditionally. Catches a regression
    // where the gate becomes mandatory and existing services break silently.
    await using var harness = _buildWorker(registry: null);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    var work = _makeWork("No.Registry.Event, Test");
    await harness.Inbox.WriteAsync(work, cts.Token);

    await harness.HandlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while ((!harness.Invoker.HasStage(LifecycleStage.PreInboxInline) || !harness.Invoker.HasStage(LifecycleStage.PostInboxInline))
           && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PreInboxInline)).IsTrue()
      .Because("With null registry, lifecycle stages must still fire (legacy behavior).");
    await Assert.That(harness.Invoker.HasStage(LifecycleStage.PostInboxInline)).IsTrue();

    await cts.CancelAsync();
  }
}
