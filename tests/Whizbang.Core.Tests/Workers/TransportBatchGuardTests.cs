// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The guard itself: a failed batch must reach the transport as a failure it can settle on
/// (abandon, never complete), in a form that cannot be mistaken for a shutdown; and a real shutdown
/// must still propagate.
/// </summary>
/// <remarks>
/// The classifier decides; this proves the decision is acted on. The guard once swallowed a failed
/// batch outright, and every transport completes a message whose handler returns, so each "abandoned"
/// batch was in fact settled as consumed and lost (#921).
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchGuard.cs</code-under-test>
[Category("Workers")]
public class TransportBatchGuardTests {

  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => Noop.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));
    private sealed class Noop : IDisposable { public static readonly Noop Instance = new(); public void Dispose() { } }
  }

  private static OperationCanceledException _statementTimeout()
    => new("Query was canceled", new PostgresException(
        messageText: "canceling statement due to user request",
        severity: "ERROR", invariantSeverity: "ERROR", sqlState: "57014"));

  [Test]
  public async Task AStatementTimeoutFailsTheBatchAsATypedSignalNotACancellationAsync() {
    var logger = new CapturingLogger();
    using var cts = new CancellationTokenSource();   // NOT canceled
    var timeout = _statementTimeout();

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(_ => throw timeout, 50, logger, cts.Token, cts.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsTypeOf<TransportBatchFailedException>()
      .Because("the transport settles on whether the handler threw: a swallowed failure is completed "
             + "and lost (#921), so the failure must reach it");
    await Assert.That(escaped is OperationCanceledException).IsFalse()
      .Because("the database reports a statement timeout as a cancellation; passed on raw, it reads as "
             + "a shutdown to every 'when not canceled' filter between here and the host, which is the "
             + "exact confusion that once stopped a host with exit 0 and no error log");
    await Assert.That(escaped!.InnerException).IsSameReferenceAs(timeout);
    await Assert.That(((TransportBatchFailedException)escaped).BatchCount).IsEqualTo(50);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsTrue()
      .Because("swallowing without logging would trade a silent death for a silent data stall");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("57014", StringComparison.Ordinal))).IsTrue()
      .Because("naming the SQLSTATE sends an operator to the query and its timeout, which is where "
             + "the problem is — 'a batch failed' sends them to the message, which is fine");
  }

  [Test]
  public async Task AnOrdinaryFaultFailsTheBatchTooAsync() {
    var logger = new CapturingLogger();
    using var cts = new CancellationTokenSource();

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(
        _ => throw new InvalidOperationException("connection reset"), 12, logger, cts.Token, cts.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsTypeOf<TransportBatchFailedException>();
    await Assert.That(escaped!.InnerException).IsTypeOf<InvalidOperationException>();
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Error)).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Message.Contains("12", StringComparison.Ordinal)).IsTrue()
      .Because("the batch size tells an operator whether this was one stray message or a systemic "
             + "failure of a full batch");
    await Assert.That(logger.Entries[0].Message.Contains("not settled as consumed", StringComparison.Ordinal)).IsTrue()
      .Because("the log must say what actually happens to the messages");
  }

  [Test]
  public async Task ARealShutdownStillPropagatesAsync() {
    var logger = new CapturingLogger();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(
        _ => throw new OperationCanceledException(cts.Token), 5, logger, cts.Token, cts.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsTypeOf<OperationCanceledException>()
      .Because("a genuine stop must unwind promptly and as itself — containing or re-typing it would "
             + "trade a silent-death bug for a shutdown that hangs, which is not an improvement");
  }

  [Test]
  public async Task TheHappyPathIsUntouchedAndSilentAsync() {
    var logger = new CapturingLogger();
    var ran = false;

    await TransportBatchGuard.RunAsync(_ => { ran = true; return Task.CompletedTask; }, 3, logger,
                                       CancellationToken.None, CancellationToken.None);

    await Assert.That(ran).IsTrue();
    await Assert.That(logger.Entries.Count).IsEqualTo(0)
      .Because("a guard that narrates successful batches would out-log the flood it was written "
             + "alongside");
  }

  [Test]
  public async Task RejectsNullArgumentsRatherThanFailingLaterAsync() {
    var logger = new CapturingLogger();
    Exception? nullBody = null;
    Exception? nullLogger = null;

    try { await TransportBatchGuard.RunAsync(null!, 1, logger, CancellationToken.None, CancellationToken.None); } catch (Exception ex) { nullBody = ex; }
    try { await TransportBatchGuard.RunAsync(_ => Task.CompletedTask, 1, null!, CancellationToken.None, CancellationToken.None); } catch (Exception ex) { nullLogger = ex; }

    await Assert.That(nullBody).IsTypeOf<ArgumentNullException>();
    await Assert.That(nullLogger).IsTypeOf<ArgumentNullException>();
  }

  [Test]
  public async Task TheSignalRequiresItsCauseAsync() {
    await Assert.That(() => new TransportBatchFailedException(1, null!)).Throws<ArgumentNullException>()
      .Because("a failed-batch signal with no cause would give an operator nothing to diagnose");
  }

  // ---------- the two tokens are NOT interchangeable ----------

  [Test]
  public async Task ACanceledBATCHTokenIsStillContainedWhileTheHostIsAliveAsync() {
    // The transport supplies its own per-batch token and cancels it for reasons that have nothing
    // to do with host shutdown — a lost session lock, a draining processor, a message-level timeout.
    // Classifying against THAT token makes every such cancellation look like a shutdown request,
    // which lets the exception escape and stops the host.
    //
    // This is not hypothetical: the first version of this guard passed the per-batch token, shipped,
    // and a host still terminated silently with the guard present in the assembly.
    var logger = new CapturingLogger();
    using var batchToken = new CancellationTokenSource();
    using var hostToken = new CancellationTokenSource();
    await batchToken.CancelAsync();          // transport canceled this batch
                                             // host is NOT stopping

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(
        _ => throw _statementTimeout(), 40, logger, batchToken.Token, hostToken.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsTypeOf<TransportBatchFailedException>()
      .Because("the HOST is alive, so this is one failed batch — classifying against the batch "
             + "token would call it a shutdown and take the process down");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsTrue();
  }

  [Test]
  public async Task TheBODYStillReceivesTheBatchTokenAsync() {
    var logger = new CapturingLogger();
    using var batchToken = new CancellationTokenSource();
    using var hostToken = new CancellationTokenSource();
    CancellationToken seen = default;

    await TransportBatchGuard.RunAsync(
      ct => { seen = ct; return Task.CompletedTask; }, 1, logger, batchToken.Token, hostToken.Token);

    await Assert.That(seen).IsEqualTo(batchToken.Token)
      .Because("the work itself must honor the transport's per-batch cancellation — only the "
             + "SHUTDOWN decision belongs to the host token");
  }

  [Test]
  public async Task AHostShutdownStillPropagatesEvenWithALiveBatchTokenAsync() {
    var logger = new CapturingLogger();
    using var batchToken = new CancellationTokenSource();
    using var hostToken = new CancellationTokenSource();
    await hostToken.CancelAsync();

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(
        _ => throw new OperationCanceledException(), 7, logger, batchToken.Token, hostToken.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsNotNull()
      .Because("a genuine stop must still unwind promptly regardless of the batch token's state");
  }
}
