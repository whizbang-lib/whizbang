using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

// A message type deliberately declared with NO namespace (Type.Namespace == null). It has to live
// outside the Whizbang.Core.Tests.Messaging block below — a type nested in a class still reports
// its enclosing NAMESPACE, not null, so this is the only way to exercise
// ReceptorInvoker._isOwnedNamespace's null/empty-namespace guard. See
// ReceptorInvokerCoverageTests.InvokeAsync_MessageTypeWithNoNamespaceAtPreOutbox_StillFiresWithoutThrowingAsync.
// CA1050 (declare types in namespaces) is intentionally violated for this one type, for that exact
// reason; suppressed rather than worked around so the violation stays visible and deliberate.
#pragma warning disable CA1050
public sealed record GlobalNamespaceCoverageCommand : IMessage;
#pragma warning restore CA1050

// IDE0161 (prefer file-scoped namespace) is a build-time error project-wide
// (EnforceCodeStyleInBuild + TreatWarningsAsErrors). Block-scoped is required here, and only
// here, so GlobalNamespaceCoverageCommand above can sit outside every namespace.
#pragma warning disable IDE0161
namespace Whizbang.Core.Tests.Messaging {

  /// <summary>
  /// Targeted coverage for specific branches in <see cref="ReceptorInvoker"/> that the broader
  /// behavioral suites (<c>ReceptorInvokerOwnedDomainFilterTests</c>,
  /// <c>ReceptorInvokerOwnedDomainTests</c>, <c>ReceptorInvocationTrackingTests</c>, etc.) already
  /// exercise for the SKIP/FIRE decision itself but never with an <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>
  /// registered — so the <c>if (_logger is not null) { Log.X(...); }</c> lines guarding each
  /// skip-path's structured log never execute. Each such test here reuses the same skip scenario
  /// as the existing suites, adds logging, and still asserts on the receptor's observable behavior
  /// (fired or not), never on the log line itself.
  /// </summary>
  /// <remarks>
  /// Setup patterns (envelope construction, fake <see cref="IReceptorRegistry"/>, scope propagation)
  /// are modeled on <c>ReceptorInvokerTagScopePropagationTests</c> and
  /// <c>EnvelopeContextReceptorInvocationRegressionTests</c>.
  /// </remarks>
  [Category("Core")]
  public class ReceptorInvokerCoverageTests {

    #region Shared Test Doubles

    private sealed class FakeReceptorRegistry : IReceptorRegistry {
      private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

      public void Add(LifecycleStage stage, ReceptorInfo receptor) {
        var key = (receptor.MessageType, stage);
        if (!_receptors.TryGetValue(key, out var list)) {
          list = [];
          _receptors[key] = list;
        }
        list.Add(receptor);
      }

      public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
        var key = (messageType, stage);
        return _receptors.TryGetValue(key, out var list) ? list : [];
      }

      public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
      public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
      public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
      public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    }

    /// <summary>Records whether — and with what caller info — a receptor actually ran.</summary>
    private sealed class FiringTracker {
      public int Count { get; private set; }
      public ICallerInfo? LastCallerInfo { get; private set; }

      public void Record(ICallerInfo? callerInfo) {
        Count++;
        LastCallerInfo = callerInfo;
      }
    }

    private sealed class StubServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
      public Guid InstanceId { get; } = Guid.NewGuid();
      public string ServiceName { get; } = serviceName;
      public string HostName => "test-host";
      public int ProcessId => 1234;

      public ServiceInstanceInfo ToInfo() => new() {
        InstanceId = InstanceId,
        ServiceName = ServiceName,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }

    private sealed class RecordingSecurityCallback : ISecurityContextCallback {
      public IScopeContext? ObservedContext { get; private set; }

      public ValueTask OnContextEstablishedAsync(
          IScopeContext context,
          IMessageEnvelope envelope,
          IServiceProvider scopedProvider,
          CancellationToken cancellationToken = default) {
        ObservedContext = context;
        return ValueTask.CompletedTask;
      }
    }

    // Message types used by exactly one test each — kept minimal (no payload) since none of the
    // scenarios below depend on the message's data, only on its type and IMessage/IEvent shape.
    private sealed record ServiceEchoCommand : IMessage;
    private sealed record OwnedEchoCommand : IMessage;
    private sealed record ForeignEchoCommand : IMessage;
    private sealed record TrackedOnceCommand : IMessage;
    private sealed record ReplaySkippedEvent : IMessage;
    private sealed record ScopedEchoCommand : IMessage;
    private sealed record PerspectiveFiredEvent : IMessage;
    private sealed record CallerTrackedCommand : IMessage;

    #endregion

    #region Helpers

    private static ReceptorInfo _receptor(Type messageType, string id, FiringTracker tracker) =>
      new(
        MessageType: messageType,
        ReceptorId: id,
        InvokeAsync: (_, _, _, callerInfo, _) => {
          tracker.Record(callerInfo);
          return ValueTask.FromResult<object?>(null);
        });

    private static MessageHop _hop(
        string serviceName,
        ScopeDelta? scope = null,
        string? callerMemberName = null) =>
      new() {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = serviceName,
          HostName = "test-host",
          ProcessId = 1234
        },
        Scope = scope,
        CallerMemberName = callerMemberName,
        CallerFilePath = callerMemberName is null ? null : "CallerSite.cs",
        CallerLineNumber = callerMemberName is null ? null : 42
      };

    // Defaults to Outbox (no LocalDispatch bit) so tests that don't care about the
    // LocalDispatch-at-PreOutbox filter don't accidentally trip it.
    private static MessageEnvelope<TMessage> _envelope<TMessage>(
        TMessage payload,
        List<MessageHop>? hops = null,
        DispatchModes mode = DispatchModes.Outbox) =>
      new() {
        MessageId = MessageId.New(),
        Payload = payload,
        Hops = hops ?? [],
        DispatchContext = new MessageDispatchContext { Mode = mode, Source = MessageSource.Local }
      };

    #endregion

    // Bug this guards: same-service PostInbox filtering (ReceptorInvoker._shouldSkipSameServicePostInbox)
    // is already covered for the skip decision itself, but never with a logger registered. If the
    // guard regressed specifically under logging (e.g. an exception thrown while building the log
    // arguments before the "return true"), a message this service already handled at LocalImmediate
    // would run its PostInbox receptor a SECOND time in any host that has logging configured —
    // which is every real host — duplicating whatever side effect the receptor performs.
    [Test]
    public async Task InvokeAsync_SameServicePostInbox_SkipsReceptorEvenWithLoggerRegisteredAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PostInboxInline, _receptor(typeof(ServiceEchoCommand), "EchoReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<IServiceInstanceProvider>(new StubServiceInstanceProvider("checkout-service"));
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new ServiceEchoCommand(), hops: [_hop("checkout-service")]);

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(tracker.Count).IsEqualTo(0)
        .Because("a message that already fired at LocalImmediate on this same service must not re-fire its PostInbox receptor, logger or not");
    }

    // Bug this guards: the owned-domain filter (ReceptorInvoker._shouldSkipOwnedDomainFilter) skips
    // an owned command reaching PreOutbox because that command already ran at LocalImmediate. If
    // this regressed under logging, any host with logging configured would re-run that command's
    // PreOutbox receptor — running the same domain logic (and its side effects) a second time.
    [Test]
    public async Task InvokeAsync_OwnedCommandAtPreOutbox_SkipsReceptorEvenWithLoggerRegisteredAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PreOutboxInline, _receptor(typeof(OwnedEchoCommand), "OwnedCommandReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<IOptions<RoutingOptions>>(
        Options.Create(new RoutingOptions().OwnDomains(typeof(OwnedEchoCommand).Namespace!)));
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new OwnedEchoCommand());

      await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

      await Assert.That(tracker.Count).IsEqualTo(0)
        .Because("an owned command reaching PreOutbox already ran at LocalImmediate — firing its receptor again duplicates that work");
    }

    // Bug this guards: the LocalDispatch double-fire guard (ReceptorInvoker._shouldSkipLocalDispatchPreOutbox)
    // stops a command already routed via the LocalDispatch flag from also firing its PreOutbox
    // receptor. If this regressed under logging, every command dispatched with LocalDispatch in a
    // logging-enabled host would run its handler twice: once locally, once again at PreOutbox.
    [Test]
    public async Task InvokeAsync_ForeignCommandWithLocalDispatchFlagAtPreOutbox_SkipsReceptorEvenWithLoggerRegisteredAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PreOutboxInline, _receptor(typeof(ForeignEchoCommand), "ForeignCommandReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<IOptions<RoutingOptions>>(
        Options.Create(new RoutingOptions().OwnDomains("Some.Unrelated.Domain")));
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new ForeignEchoCommand(), mode: DispatchModes.Local);

      await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

      await Assert.That(tracker.Count).IsEqualTo(0)
        .Because("the command already fired locally via the LocalDispatch flag — PreOutbox must not run its receptor a second time");
    }

    // Bug this guards: LifecycleStageTracker dedup (ReceptorInvoker._tryClaimStageTracker) stops a
    // second worker from re-running a stage another worker already claimed for the same message. If
    // this regressed under logging, two workers racing on the same inbox message in a logging-enabled
    // host would both run its lifecycle receptors — doubling every side effect they perform.
    [Test]
    public async Task InvokeAsync_StageAlreadyClaimedByAnotherWorker_SkipsReceptorEvenWithLoggerRegisteredAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.LocalImmediateInline, _receptor(typeof(TrackedOnceCommand), "TrackedReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton(new LifecycleStageTracker());
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new TrackedOnceCommand());

      await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);
      await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

      await Assert.That(tracker.Count).IsEqualTo(1)
        .Because("the second worker to reach the same message+stage must be a no-op, not a second firing, even with logging enabled");
    }

    // Bug this guards: the replay filter (ReceptorInvoker._filterForReplayMode, logged via
    // _logSkippedReplayModeFilter) drops non-idempotent receptors for events already processed in a
    // prior pass. If this regressed under logging, replaying old events to rebuild a perspective in
    // any logging-enabled host would re-run every non-idempotent receptor for already-handled
    // events — duplicating side effects (emails, external calls) that must never run twice.
    [Test]
    public async Task InvokeAsync_ReplayOfAlreadyProcessedEvent_SkipsNonIdempotentReceptorEvenWithLoggerRegisteredAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PostInboxInline, _receptor(typeof(ReplaySkippedEvent), "NonIdempotentReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new ReplaySkippedEvent());
      var context = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostInboxInline,
        ProcessingMode = ProcessingMode.Replay,
        IsNewEvent = false
      };

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline, context);

      await Assert.That(tracker.Count).IsEqualTo(0)
        .Because("a non-idempotent receptor must not re-fire for an event replay already processed in a prior pass");
    }

    // Bug this guards: when no IMessageSecurityContextProvider is registered but the envelope still
    // carries scope from an upstream hop, ReceptorInvoker._promoteScopeWithPropagationAsync promotes
    // that scope and invokes every registered ISecurityContextCallback with it. If this regressed, a
    // consumer's tenant-context callback (e.g. one that opens a per-tenant database connection) would
    // silently never run in that configuration, and every downstream receptor would run without
    // tenant context.
    [Test]
    public async Task InvokeAsync_NoSecurityProviderButEnvelopeCarriesScope_InvokesCallbackWithPromotedScopeAsync() {
      var callback = new RecordingSecurityCallback();
      var registry = new FakeReceptorRegistry();

      var services = new ServiceCollection();
      services.AddSingleton<IMessageContextAccessor, MessageContextAccessor>();
      services.AddSingleton<ISecurityContextCallback>(callback);
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var scope = ScopeDelta.FromSecurityContext(new SecurityContext { UserId = "user-1", TenantId = "tenant-1" });
      var envelope = _envelope(new ScopedEchoCommand(), hops: [_hop("upstream-service", scope: scope)]);

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(callback.ObservedContext).IsNotNull();
      await Assert.That(callback.ObservedContext!.Scope.UserId).IsEqualTo("user-1")
        .Because("the callback must see the scope extracted from the envelope's hop even though no security provider established one");
      await Assert.That(callback.ObservedContext!.Scope.TenantId).IsEqualTo("tenant-1");
    }

    // Bug this guards: perspective-scoped stages are exempt from the double-fire guard
    // (ReceptorInvoker._isDoubleFireAndSkipOrThrowAsync) because the SAME receptor legitimately
    // fires once per perspective per event. If this exemption regressed, every perspective after the
    // first to process a given event would see a "prior invocation" for that receptor and — with
    // OnDoubleFire=Throw as configured here — crash the whole pipeline with
    // DuplicateReceptorFireException instead of running that perspective's own invocation.
    [Test]
    public async Task InvokeAsync_PerspectiveScopedStageWithPriorInvocationRecorded_StillFiresDespiteThrowConfiguredAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PrePerspectiveInline, _receptor(typeof(PerspectiveFiredEvent), "PerspectiveReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
      services.Configure<WhizbangOptions>(o => o.Guardrails.OnDoubleFire = DoubleFireBehavior.Throw);
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new PerspectiveFiredEvent());
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "PerspectiveReceptor",
          Stage = LifecycleStage.PrePerspectiveInline,
          CompletedAt = DateTimeOffset.UtcNow,
          Duration = TimeSpan.Zero,
          ServiceName = "other-perspective-run"
        }
      ];

      await invoker.InvokeAsync(envelope, LifecycleStage.PrePerspectiveInline);

      await Assert.That(tracker.Count).IsEqualTo(1)
        .Because("perspective-scoped stages let the same receptor fire once per perspective, even though the envelope already carries an invocation record for it");
    }

    // Bug this guards: ReceptorInvoker._logCallerInfo logs (and, upstream, _extractCallerInfo /
    // ReceptorInfo.InvokeAsync propagates) the caller info captured at the original dispatch site. If
    // this propagation regressed, a receptor would lose the "who dispatched this and from where"
    // trail entirely — exactly the traceability this exists to preserve when debugging an unexpected
    // firing.
    [Test]
    public async Task InvokeAsync_EnvelopeCarriesDispatchSiteCallerInfo_ReceptorReceivesItAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.LocalImmediateInline, _receptor(typeof(CallerTrackedCommand), "CallerAwareReceptor", tracker));

      var services = new ServiceCollection();
      services.AddLogging();
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(
        new CallerTrackedCommand(),
        hops: [_hop("dispatch-service", callerMemberName: "PlaceOrderAsync")]);

      await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

      await Assert.That(tracker.Count).IsEqualTo(1);
      await Assert.That(tracker.LastCallerInfo).IsNotNull();
      await Assert.That(tracker.LastCallerInfo!.CallerMemberName).IsEqualTo("PlaceOrderAsync")
        .Because("the receptor must see the exact caller captured at the dispatch site, not a blank/default value");
      await Assert.That(tracker.LastCallerInfo!.CallerLineNumber).IsEqualTo(42);
    }

    // Bug this guards: ReceptorInvoker._isOwnedNamespace short-circuits on a null/empty namespace
    // before ever touching the owned-domains set. If that guard regressed, a message type with no
    // namespace would reach the prefix-matching loop and call string.StartsWith on a null namespace —
    // throwing a NullReferenceException and crashing PreOutbox for that message, instead of simply
    // treating it as not-owned like any other foreign type.
    [Test]
    public async Task InvokeAsync_MessageTypeWithNoNamespaceAtPreOutbox_StillFiresWithoutThrowingAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PreOutboxInline, _receptor(typeof(GlobalNamespaceCoverageCommand), "GlobalNamespaceReceptor", tracker));

      var services = new ServiceCollection();
      services.AddSingleton<IOptions<RoutingOptions>>(
        Options.Create(new RoutingOptions().OwnDomains("Some.Owned.Domain")));
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new GlobalNamespaceCoverageCommand());

      await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

      await Assert.That(tracker.Count).IsEqualTo(1)
        .Because("a message type with no namespace can never match an owned domain, so it must be treated as foreign and still fire, not crash or vanish");
    }

    // Bug this guards: every skip path above returns quietly. A receptor that does not fire looks
    // exactly like a receptor that fired and did nothing, so when someone reports "my handler never
    // ran", this log line is the only thing that says which filter dropped it and for which message.
    // The tests above prove the skip still happens with logging configured; this one proves the
    // resulting diagnostic is actually usable -- a line that says "skipped" without naming the
    // message sends the reader back to guessing.
    [Test]
    public async Task InvokeAsync_SkippedByAFilter_SaysWhichMessageItDroppedAsync() {
      var tracker = new FiringTracker();
      var registry = new FakeReceptorRegistry();
      registry.Add(LifecycleStage.PostInboxInline, _receptor(typeof(ServiceEchoCommand), "EchoReceptor", tracker));

      var captured = new CapturingLoggerProvider();
      var services = new ServiceCollection();
      services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(captured));
      services.AddSingleton<IServiceInstanceProvider>(new StubServiceInstanceProvider("checkout-service"));
      var provider = services.BuildServiceProvider();

      var invoker = new ReceptorInvoker(registry, provider);
      var envelope = _envelope(new ServiceEchoCommand(), hops: [_hop("checkout-service")]);

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(tracker.Count).IsEqualTo(0);
      var skipLine = captured.Messages.FirstOrDefault(m => m.Contains("Skipped", StringComparison.Ordinal));
      await Assert.That(skipLine).IsNotNull()
        .Because("a silent skip with no diagnostic is indistinguishable from a receptor that ran and did nothing");
      await Assert.That(skipLine!).Contains(nameof(ServiceEchoCommand))
        .Because("the reader needs to know which message was dropped, not merely that something was");
      await Assert.That(skipLine!).Contains(envelope.MessageId.Value.ToString())
        .Because("naming the type alone cannot distinguish one dropped message from thousands of the same type");
    }

    /// <summary>Captures formatted log output so a diagnostic's content can be asserted, not just its existence.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider {
      private readonly List<string> _messages = [];
      public IReadOnlyList<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
      public ILogger CreateLogger(string categoryName) => new Sink(_messages);
      public void Dispose() { }

      private sealed class Sink(List<string> messages) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
          lock (messages) { messages.Add(formatter(state, exception)); }
        }
      }
    }
  }
}
#pragma warning restore IDE0161
