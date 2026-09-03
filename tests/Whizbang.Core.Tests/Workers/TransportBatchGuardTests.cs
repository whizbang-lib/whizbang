using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The guard itself: a failed batch must be contained, and a real shutdown must still propagate.
/// </summary>
/// <remarks>
/// The classifier decides; this proves the decision is acted on. Testing the classifier alone would
/// leave the actual containment — the thing that keeps the process alive — unexercised.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchGuard.cs</code-under-test>
[Category("Workers")]
public class TransportBatchGuardTests {

  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => Noop.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
      => Entries.Add((level, fmt(state, ex)));
    private sealed class Noop : IDisposable { public static readonly Noop Instance = new(); public void Dispose() { } }
  }

  private static OperationCanceledException _statementTimeout()
    => new("Query was canceled", new PostgresException(
        messageText: "canceling statement due to user request",
        severity: "ERROR", invariantSeverity: "ERROR", sqlState: "57014"));

  [Test]
  public async Task AStatementTimeoutDoesNotEscapeAsync() {
    var logger = new CapturingLogger();
    var cts = new CancellationTokenSource();   // NOT canceled

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(_ => throw _statementTimeout(), 50, logger, cts.Token, cts.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsNull()
      .Because("this exact exception escaping ExecuteAsync is what stopped a host gracefully with "
             + "exit 0 and no error log — containing it here is the entire fix");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsTrue()
      .Because("swallowing without logging would trade a silent death for a silent data stall");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("57014", StringComparison.Ordinal))).IsTrue()
      .Because("naming the SQLSTATE sends an operator to the query and its timeout, which is where "
             + "the problem is — 'a batch failed' sends them to the message, which is fine");
  }

  [Test]
  public async Task AnOrdinaryFaultDoesNotEscapeEitherAsync() {
    var logger = new CapturingLogger();
    var cts = new CancellationTokenSource();

    Exception? escaped = null;
    try {
      await TransportBatchGuard.RunAsync(
        _ => throw new InvalidOperationException("connection reset"), 12, logger, cts.Token, cts.Token);
    } catch (Exception ex) { escaped = ex; }

    await Assert.That(escaped).IsNull();
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Error)).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Message.Contains("12", StringComparison.Ordinal)).IsTrue()
      .Because("the batch size tells an operator whether this was one stray message or a systemic "
             + "failure of a full batch");
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

    await Assert.That(escaped).IsNotNull()
      .Because("a genuine stop must unwind promptly — containing it would trade a silent-death bug "
             + "for a shutdown that hangs, which is not an improvement");
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

    await Assert.That(escaped).IsNull()
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
