using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Coverage round 23 — targets <see cref="StartupReadyService"/>'s private
/// <c>_describePipeline</c> helper as a unit: what an operator sees narrated while
/// <see cref="StartupReadyService.StartedAsync"/> is still blocked waiting on the pipeline itself
/// (as opposed to a registered <see cref="IStartupReadinessContributor"/>, which is narrated by a
/// different describe delegate and already covered elsewhere).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupReadiness.cs</code-under-test>
[Category("Startup")]
public class StartupReadinessCoverageTests {

  private static StartupStepDescriptor _step(string name, bool blocking = true) =>
    new() { Name = name, Blocking = blocking };

  private sealed class _narrationLogger : Microsoft.Extensions.Logging.ILogger<StartupReadyService> {
    private readonly List<string> _entries = [];
    private readonly Lock _lock = new();
    public IReadOnlyList<string> Entries {
      get {
        lock (_lock) {
          return [.. _entries];
        }
      }
    }
    IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _entries.Add(formatter(state, exception));
      }
    }
  }

  /// <summary>
  /// If <c>_describePipeline</c> stops naming the specific pending BLOCKING step (and its status),
  /// an operator staring at a hung deploy sees only "still waiting on the startup pipeline" with
  /// nothing to bisect against — exactly the silent-hang failure mode issue #493 exists to prevent.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StartedAsync_BlockingStepStillPending_NarratesTheStepNameAndStatusAsync(
      CancellationToken cancellationToken) {
    var state = new StartupPipelineState();
    await state.OnRunStartingAsync(new StartupRunPlan([_step("Migrate")]), cancellationToken);
    var logger = new _narrationLogger();
    var service = new StartupReadyService(state, new StartupReadySignal(), logger: logger) {
      WaitProbeInterval = TimeSpan.FromMilliseconds(15),
    };
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var started = service.StartedAsync(cts.Token);
    while (!logger.Entries.Any(e => e.Contains("Migrate (Pending)"))) {
      cancellationToken.ThrowIfCancellationRequested();
      await Task.Delay(15, cancellationToken);
    }

    await Assert.That(logger.Entries.Any(e => e.Contains("startup pipeline step(s) Migrate (Pending)"))).IsTrue()
      .Because("the pending BLOCKING step's name and status must appear verbatim in the narration, or a " +
               "hung deploy gives an operator nothing to act on beyond \"still waiting\"");

    await cts.CancelAsync();
    try { await started; } catch (OperationCanceledException) { /* fail-closed, as designed */ }
  }

  /// <summary>
  /// A run driven without ever announcing its plan (an observer sees <c>OnStepStartingAsync</c>
  /// directly, with no preceding <c>OnRunStartingAsync</c>) leaves <c>HasRunStarted</c> true but the
  /// planned-step list empty — readiness then never fires for this run at all. If the "no pending
  /// steps" description regresses (throws, or mislabels this as "no run has started"), an operator
  /// gets a misleading diagnosis for a host that is stuck for a structurally different reason
  /// (missing plan announcement) than "hasn't started" or "a step won't finish".
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StartedAsync_RunStartedWithoutAPlanAnnouncement_NarratesTheEmptyPipelineAsync(
      CancellationToken cancellationToken) {
    var state = new StartupPipelineState();
    await state.OnStepStartingAsync(new StartupStepContext(_step("SomeStep")), cancellationToken);
    var logger = new _narrationLogger();
    var service = new StartupReadyService(state, new StartupReadySignal(), logger: logger) {
      WaitProbeInterval = TimeSpan.FromMilliseconds(15),
    };
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var started = service.StartedAsync(cts.Token);
    while (!logger.Entries.Any(e => e.Contains("waiting on the startup pipeline."))) {
      cancellationToken.ThrowIfCancellationRequested();
      await Task.Delay(15, cancellationToken);
    }

    await Assert.That(logger.Entries.Any(e => e.Contains("waiting on the startup pipeline."))).IsTrue()
      .Because("HasRunStarted=true with zero planned steps must describe itself as \"the startup pipeline\" " +
               "(no pending step to name) rather than crashing the narration or mislabeling it as " +
               "\"no run has started yet\" — those are different diagnoses for an operator");

    await cts.CancelAsync();
    try { await started; } catch (OperationCanceledException) { /* fail-closed, as designed */ }
  }
}
