using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="SlidingWindowInboxBatchStrategy"/> paths the primary suite
/// (<see cref="SlidingWindowInboxBatchStrategyTests"/>) doesn't reach: the hard-shutdown branch
/// of <see cref="SlidingWindowInboxBatchStrategy.FlushAndStopAsync"/> — a caller-supplied
/// cancellation token firing while a per-stream buffer's flush is still hung — and the drain
/// loop's own cooperative return once that forced cancellation lands.
/// </summary>
/// <docs>extending/internals/event-ordering-invariant</docs>
public class SlidingWindowInboxBatchStrategyCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  private InboxMessage _makeMessage(Guid? streamId = null) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
    };
  }

  /// <summary>Captures error-level messages — used to prove a shutdown-forced cancellation of an
  /// in-flight flush is never mistaken for a flush failure.</summary>
  private sealed class _RecordingLogger : ILogger<SlidingWindowInboxBatchStrategy> {
    private readonly Lock _lock = new();
    private readonly List<string> _errors = [];

    public List<string> Errors {
      get { lock (_lock) { return [.. _errors]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Error) {
        lock (_lock) { _errors.Add(formatter(state, exception)); }
      }
    }
  }

  /// <summary>
  /// Covers the hard-shutdown pairing: when the caller's <c>FlushAndStopAsync</c> token fires
  /// while a per-stream buffer's flush is still hung, the strategy must force-cancel its
  /// internal token to unstick the drain task rather than waiting for it forever, AND the drain
  /// task's own OperationCanceledException-during-shutdown catch must return quietly instead of
  /// falling into the generic exception handler.
  /// </summary>
  /// <remarks>
  /// If the force-cancel regressed, a hard-shutdown deadline would leave a hung drain task
  /// running forever with nothing left to unstick it — <c>FlushAndStopAsync</c> would still
  /// return (its own wait already threw), but the leaked task's eventual outcome is lost and the
  /// process cannot exit cleanly while it survives. If the drain loop's own cooperative return
  /// regressed instead, that same forced cancellation would be logged as a flush FAILURE —
  /// turning an intentional, already-handled shutdown into false-positive error-log noise that
  /// pages an operator for nothing.
  /// </remarks>
  [Test]
  [Timeout(30000)]
  public async Task FlushAndStopAsync_CallerTokenFiresWhileFlushIsHung_ForceCancelsWithoutLoggingFailureAsync(
      CancellationToken testToken) {
    var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new _RecordingLogger();

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: async (msgs, ct) => {
        flushStarted.TrySetResult();
        // Hangs until the strategy's own internal cancellation source is force-canceled by
        // FlushAndStopAsync's hard-shutdown branch — never completes on its own.
        await Task.Delay(Timeout.Infinite, ct);
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
      },
      logger: logger);

    await sut.AppendAsync(_makeMessage(), testToken);
    await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    using var callerCts = new CancellationTokenSource();
    await callerCts.CancelAsync();

    // The caller's token is already canceled and the flush is still hung, so awaiting the
    // drain workers with that token must throw immediately — caught internally, forcing the
    // strategy's own hard-cancel — rather than this call ever throwing out to us.
    await sut.FlushAndStopAsync(callerCts.Token).WaitAsync(TimeSpan.FromSeconds(10), testToken);

    // Give the drain task's own catch (now unblocked by the forced cancellation) a moment to
    // run — it either returns quietly or, if regressed, logs a spurious failure.
    await Task.Delay(200, testToken);

    await Assert.That(logger.Errors).IsEmpty()
      .Because("a shutdown-forced cancellation of an in-flight flush is not a flush failure and must never be logged as one");
  }
}
