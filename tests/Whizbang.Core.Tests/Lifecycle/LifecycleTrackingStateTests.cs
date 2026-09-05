using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Covers <see cref="LifecycleTrackingState"/> — the per-event state machine that decides
/// how many times a receptor runs, whether a stage blocks the pipeline, and what happens
/// when a detached stage fails.
/// </summary>
/// <remarks>
/// These are the exactly-once and isolation guarantees the lifecycle advertises. A
/// regression here does not throw: it silently double-invokes a receptor, or drops a
/// detached failure on the floor, which is precisely the class of bug that only shows up
/// as duplicated side effects in production.
/// </remarks>
[Category("Core")]
[Category("Lifecycle")]
public class LifecycleTrackingStateTests {

  private sealed record ProbeEvent(string Data) : IEvent;

  /// <summary>Records every stage the tracking asks it to invoke.</summary>
  private sealed class RecordingInvoker : IReceptorInvoker {
    private readonly Lock _gate = new();
    public List<LifecycleStage> Stages { get; } = [];
    public Exception? Throw { get; set; }
    public LifecycleStage? ThrowOnlyFor { get; set; }

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      lock (_gate) {
        Stages.Add(stage);
      }
      if (Throw is not null && (ThrowOnlyFor is null || ThrowOnlyFor == stage)) {
        throw Throw;
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingLogger : ILogger {
    private readonly Lock _gate = new();
    public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_gate) {
        Entries.Add((logLevel, formatter(state, exception), exception));
      }
    }
  }

  private static (LifecycleTrackingState Tracking, ServiceProvider Provider, RecordingInvoker Invoker, RecordingLogger Logger)
      _build(RecordingInvoker? invoker = null, bool registerInvoker = true) {
    var recording = invoker ?? new RecordingInvoker();
    var logger = new RecordingLogger();

    var services = new ServiceCollection();
    services.AddLogging();
    if (registerInvoker) {
      services.AddSingleton<IReceptorInvoker>(recording);
    }
    var provider = services.BuildServiceProvider();

    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), new ProbeEvent("payload"), []);
    var tracking = new LifecycleTrackingState(
      eventId: Guid.NewGuid(),
      envelope: envelope,
      entryStage: LifecycleStage.LocalImmediateInline,
      source: MessageSource.Local,
      streamId: null,
      perspectiveType: null,
      logger: logger);

    return (tracking, provider, recording, logger);
  }

  [Test]
  public async Task AdvancingToTheSameStageTwice_InvokesReceptorsOnceAsync() {
    // The stage guard is the exactly-once guarantee. Without it a retried or re-entered
    // pipeline runs every receptor for that stage a second time -- duplicate emails,
    // duplicate charges -- and nothing throws to reveal it.
    var (tracking, provider, invoker, _) = _build();
    using var _p = provider;

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeInline, provider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeInline, provider, CancellationToken.None);

    await Assert.That(invoker.Stages.Count(s => s == LifecycleStage.PreDistributeInline)).IsEqualTo(1)
      .Because("each stage fires at most once per event, however many times it is advanced to");
  }

  [Test]
  public async Task AnInlineStage_ChainsImmediateDetachedAfterItAsync() {
    // ImmediateDetached is contractually part of the blocking pipeline for inline stages:
    // it runs after the stage and is awaited, so a receptor registered there is guaranteed
    // to have completed before the pipeline moves on.
    var (tracking, provider, invoker, _) = _build();
    using var _p = provider;

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeInline, provider, CancellationToken.None);

    await Assert.That(invoker.Stages).IsEquivalentTo(
      new List<LifecycleStage> { LifecycleStage.PreDistributeInline, LifecycleStage.ImmediateDetached });
  }

  [Test]
  public async Task PostLifecycleInline_CompletesTheTrackingAsync() {
    var (tracking, provider, _, _) = _build();
    using var _p = provider;

    await Assert.That(tracking.IsComplete).IsFalse();

    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, provider, CancellationToken.None);

    await Assert.That(tracking.IsComplete).IsTrue();
  }

  [Test]
  public async Task AfterCompletion_FurtherStagesAreIgnoredAsync() {
    // Completion is terminal. A late stage arriving after PostLifecycleInline must not
    // reopen the event and invoke receptors against state that has already been finalized.
    var (tracking, provider, invoker, _) = _build();
    using var _p = provider;

    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, provider, CancellationToken.None);
    var afterCompletion = invoker.Stages.Count;

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeInline, provider, CancellationToken.None);

    await Assert.That(invoker.Stages.Count).IsEqualTo(afterCompletion)
      .Because("a completed tracking accepts no further stages");
    await Assert.That(invoker.Stages).DoesNotContain(LifecycleStage.PreDistributeInline);
  }

  [Test]
  public async Task WithoutAReceptorInvoker_TheStageIsSkippedWithoutThrowingAsync() {
    // A scope with no invoker registered is a legitimate configuration (a host with no
    // receptors). It must degrade to a no-op rather than tearing down the pipeline.
    var (tracking, provider, _, _) = _build(registerInvoker: false);
    using var _p = provider;

    await Assert.That(async () =>
        await tracking.AdvanceToAsync(LifecycleStage.PreDistributeInline, provider, CancellationToken.None))
      .ThrowsNothing();
  }

  [Test]
  public async Task ADetachedStage_RunsOutOfBandAndIsDrainableAsync() {
    // Detached stages deliberately do not block the pipeline, so the only deterministic
    // way to observe them is the drain. This is also what shutdown relies on to avoid
    // killing in-flight receptors.
    var (tracking, provider, invoker, _) = _build();
    using var _p = provider;

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeDetached, provider, CancellationToken.None);
    await tracking.DrainDetachedAsync();

    await Assert.That(invoker.Stages).Contains(LifecycleStage.PreDistributeDetached)
      .Because("the detached work runs for real, just not on the caller's thread");
  }

  [Test]
  public async Task ADetachedStageThatThrows_IsLoggedRatherThanLostAsync() {
    // A detached receptor that throws before its own telemetry runs would otherwise fail
    // completely invisibly -- no exception reaches the pipeline, because nothing awaits it.
    // The catch exists to make that failure observable, so assert it actually reports.
    var failing = new RecordingInvoker {
      Throw = new InvalidOperationException("detached receptor failed"),
      ThrowOnlyFor = LifecycleStage.PreDistributeDetached,
    };
    var (tracking, provider, _, logger) = _build(failing);
    using var _p = provider;

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeDetached, provider, CancellationToken.None);
    await tracking.DrainDetachedAsync();

    var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
    await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1)
      .Because("a detached failure that is not logged is a failure nobody ever learns about");
    await Assert.That(errors[0].Error).IsNotNull();
  }

  [Test]
  public async Task DrainingWithNoDetachedWork_CompletesImmediatelyAsync() {
    var (tracking, provider, _, _) = _build();
    using var _p = provider;

    await Assert.That(async () => await tracking.DrainDetachedAsync()).ThrowsNothing();
  }

  [Test]
  public async Task AdvancingAStage_MovesCurrentStageAndTouchesActivityAsync() {
    // LastActivityUtc drives stale-tracking cleanup: a sliding window that must move on
    // every transition, or a busy event gets reaped as abandoned.
    var (tracking, provider, _, _) = _build();
    using var _p = provider;
    var before = tracking.LastActivityUtc;

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeInline, provider, CancellationToken.None);

    await Assert.That(tracking.CurrentStage).IsEqualTo(LifecycleStage.PreDistributeInline);
    await Assert.That(tracking.LastActivityUtc).IsGreaterThanOrEqualTo(before);
  }
}
