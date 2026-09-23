using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="SlidingWindowOutboxBatchStrategy"/>'s shutdown and eviction paths:
/// the flush that observes the forced stop, the idle sweep that lands after shutdown, the drain
/// loop's outer catch, and the sweep's tolerance of a worker that died.
/// </summary>
public class SlidingWindowOutboxBatchStrategyCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Target: src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs:140 — `return;` in
  // the OperationCanceledException handler around the flush
  // call. FlushAndStopAsync always completes the stream's channel writer before it ever cancels
  // _stopCts, so a flush callback that itself awaits _stopCts's own token (as production flush
  // callbacks resolving a DI scope legitimately can, via the token this class hands them) is the
  // one deterministic way to observe that cancellation from inside the flush — no race, since
  // Task.Delay(Timeout.Infinite, ct) has no other way to complete. If this catch let the
  // exception escape uncaught, the drain task would fault instead of returning cleanly, and
  // Task.WhenAll(workers) inside FlushAndStopAsync would surface an unrelated fault instead of
  // the clean shutdown callers rely on.
  [Test]
  [Timeout(15000)]
  public async Task FlushObservingStopToken_DuringForcedShutdown_ReturnsWithoutFaultingAsync(
      CancellationToken cancellationToken) {
    var flushEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: async (_, ct) => {
        flushEntered.TrySetResult();
        // The ONLY way this ever completes is via ct (== the strategy's own _stopCts) being
        // canceled -- there is no competing "normal" completion path, so there is no race.
        await Task.Delay(Timeout.Infinite, ct);
      },
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(20),
        MaxWait = TimeSpan.FromMilliseconds(100),
        MaxSize = 100,
      });

    await sut.AppendAsync(_make(_idProvider.NewGuid()), cancellationToken);
    await flushEntered.Task.WaitAsync(cancellationToken);

    using var stopCts = new CancellationTokenSource();
    await stopCts.CancelAsync();

    // FlushAndStopAsync's own WaitAsync(cancellationToken) throws immediately on the pre-canceled
    // token, which is what makes it cancel the strategy's internal _stopCts -- the token the
    // pending flush above is blocked on. Must return promptly, not hang or throw.
    await sut.FlushAndStopAsync(stopCts.Token).WaitAsync(cancellationToken);

    await Assert.That(async () => await sut.AppendAsync(_make(_idProvider.NewGuid())))
      .ThrowsExactly<ObjectDisposedException>()
      .Because("the strategy must have fully stopped, not merely returned from a faulted drain task");
  }

  /// <summary>
  /// The sweep timer is periodic and its callback is fire-and-forget, so a tick can land after
  /// <see cref="SlidingWindowOutboxBatchStrategy.FlushAndStopAsync"/> has already completed every
  /// writer and disposed the strategy's internal cancellation source. Sweeping then would
  /// re-complete completed writers and await already-finished workers on a torn-down object,
  /// during host shutdown. The disposed guard is what makes that tick a no-op.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_AfterShutdown_LeavesTheBuffersAloneAsync(CancellationToken cancellationToken) {
    var clock = new FakeTimeProvider();
    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (_, _) => Task.CompletedTask,
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(20),
        MaxWait = TimeSpan.FromMilliseconds(100),
        MaxSize = 100,
        // Long enough that nothing fires on its own — the sweep under test is driven explicitly.
        IdleSweepInterval = TimeSpan.FromMinutes(5),
        IdleEvictionWindow = TimeSpan.FromSeconds(30),
      },
      timeProvider: clock);

    await sut.AppendAsync(_make(_idProvider.NewGuid()), cancellationToken);
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("the buffer has to be really mapped, or the assertion below would hold vacuously");

    await sut.FlushAndStopAsync(CancellationToken.None);
    // Every mapped buffer is now far past its eviction window: a running sweep WOULD evict it.
    clock.Advance(TimeSpan.FromMinutes(10));

    await sut.RunIdleSweepNowForTestAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("a sweep that lands after shutdown must return without touching the buffers — "
             + "the clock was advanced past the eviction window, so without the disposed guard "
             + "this stream would have been evicted and its finished worker awaited again");
  }

  private OutboxMessage _make(Guid? streamId) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = messageId,
      StreamId = streamId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = [],
      },
    };
  }

  // ============================================================
  // The drain loop's outer catch, and the sweep's tolerance of a dead worker
  // ============================================================

  // Both tests below work by making the LOGGING SINK throw. The error path a drain worker takes
  // when a flush fails is not itself infallible: a sink bound to the host's shutdown token throws
  // OperationCanceledException as the host stops, and a sink whose provider has already been torn
  // down throws something else. Those are the only exceptions that escape the flush-failure
  // handler, and so the only way anything reaches the loop's outer catch at all.

  // A cancellation that escapes the flush-failure handler must end the drain quietly. If it
  // escaped the loop as well, the worker task would fault, and FlushAndStopAsync folds a faulted
  // worker into the same catch it uses for a caller-canceled shutdown — so the fault would be
  // swallowed there too and surface only as an unobserved exception, long after the shutdown that
  // caused it and with nothing tying it back.
  [Test]
  [Timeout(30000)]
  public async Task DrainWorker_CancellationEscapingTheFlushFailureHandler_EndsTheLoopQuietlyAsync(
      CancellationToken cancellationToken) {
    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (_, _) => Task.FromException(new InvalidOperationException("store unavailable")),
      logger: new ThrowingSink(() => new OperationCanceledException("log sink shutting down")),
      options: _oneMessagePerBatch(),
      timeProvider: new FakeTimeProvider());

    await sut.AppendAsync(_make(_idProvider.NewGuid()), cancellationToken);

    await Assert.That(async () => await sut.WhenWorkersStoppedForTests()).ThrowsNothing()
      .Because("the drain loop absorbs a cancellation rather than faulting; a faulted worker here "
             + "is an unobserved exception that nothing in this type ever reports");
  }

  // The idle sweep is the only thing that bounds this type's memory. It awaits each evicted
  // stream's worker to let in-flight work finish, and a worker that died is exactly what it will
  // meet after a storm of flush failures — if that killed the sweep, every stream after the dead
  // one would stay mapped forever and the leak the sweep exists to prevent comes back.
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_WorkersDied_StillEvictsEveryIdleStreamAsync(
      CancellationToken cancellationToken) {
    var clock = new FakeTimeProvider();
    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (_, _) => Task.FromException(new InvalidOperationException("store unavailable")),
      logger: new ThrowingSink(() => new InvalidOperationException("log provider disposed")),
      options: _oneMessagePerBatch(),
      timeProvider: clock);

    await sut.AppendAsync(_make(_idProvider.NewGuid()), cancellationToken);
    await sut.AppendAsync(_make(_idProvider.NewGuid()), cancellationToken);

    // Deterministic wait: both workers have run to their end by the time this completes.
    await Assert.That(async () => await sut.WhenWorkersStoppedForTests())
      .Throws<InvalidOperationException>()
      .Because("this arranges the precondition the sweep has to survive — a worker whose own error "
             + "path threw, so its task is faulted rather than finished");

    clock.Advance(TimeSpan.FromMinutes(10));
    await sut.RunIdleSweepNowForTestAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0)
      .Because("the sweep has to keep going past a dead worker; stopping at the first one would "
             + "leave every later stream mapped for the life of the process");
  }

  private static SlidingWindowOutboxOptions _oneMessagePerBatch() => new() {
    // One message per batch: the batcher yields on the size bound without ever consulting the
    // clock, so the flush below runs with no wall-clock wait and no fake-timer choreography.
    MaxSize = 1,
    SlidingWindow = TimeSpan.FromMilliseconds(50),
    MaxWait = TimeSpan.FromSeconds(1),
    IdleSweepInterval = TimeSpan.FromMinutes(5),
    IdleEvictionWindow = TimeSpan.FromSeconds(30),
  };

  /// <summary>A logging sink that throws whatever it is told to throw.</summary>
  private sealed class ThrowingSink(Func<Exception> failure) : ILogger<SlidingWindowOutboxBatchStrategy> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
      => throw failure();
  }
}
