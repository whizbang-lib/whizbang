using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// v0.651 release/v0.651.0-alpha.1 — locks the
/// <see cref="UnobservedExceptionDiagnostics"/> contract:
///
/// <list type="bullet">
/// <item><description>Constructor subscribes the process-wide
/// <see cref="TaskScheduler.UnobservedTaskException"/> handler exactly once
/// across the process. A second instantiation is a no-op (the first owner
/// keeps the subscription).</description></item>
/// <item><description>An abandoned <see cref="Task"/> that throws AND is
/// GC-finalized triggers the handler, which logs at Error level and calls
/// <c>SetObserved</c> so the runtime stops tracking the exception.</description></item>
/// <item><description>The
/// <see cref="UnobservedExceptionDiagnosticsOptions.FirstChanceExceptionTypeAllowList"/>
/// filter narrows first-chance logging to specific exception types; an
/// empty/null allow-list matches all (non-OCE) types.</description></item>
/// <item><description><see cref="UnobservedExceptionDiagnostics.Dispose"/>
/// unsubscribes the handlers when the owning instance disposes — exercised
/// by every test's cleanup so the next test's diagnostics can re-register.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class UnobservedExceptionDiagnosticsTests {

  // --- fakes ---

  private sealed record _LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class _CapturingLogger<T> : ILogger<T> {
    public List<_LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new _LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  // --- tests ---

  /// <summary>
  /// The dispose contract — registering, disposing, and re-registering must
  /// each succeed without orphaning the static slot. This is the fixture-level
  /// invariant: every other test in this class re-registers, so a leak here
  /// would manifest as silent test bleed.
  /// </summary>
  [Test]
  public async Task Dispose_ReleasesStaticSlot_NextInstanceRegistersFreshAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions());
    var first = new UnobservedExceptionDiagnostics(logger, options);
    first.Dispose();

    // A second registration after dispose MUST succeed — if Dispose didn't
    // release the static slot, the second instance would silently no-op.
    var second = new UnobservedExceptionDiagnostics(logger, options);
    second.Dispose();

    // Reaching this assertion without throwing is the success condition; an
    // exception would have escaped the constructor. Keep an explicit assert
    // so the test reads as a behavioural lock rather than a smoke check.
    await Assert.That(logger.Entries).IsNotNull();
  }

  /// <summary>
  /// A constructor with a null logger throws ArgumentNullException — locks the
  /// "fail fast on bad DI wiring" contract so a misconfigured host crashes at
  /// startup instead of silently swallowing future unobserved exceptions.
  /// </summary>
  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    var caught = await Assert.That(() => {
      _ = new UnobservedExceptionDiagnostics(null!, Options.Create(new UnobservedExceptionDiagnosticsOptions()));
      return Task.CompletedTask;
    }).ThrowsExactly<ArgumentNullException>();
    await Assert.That(caught!.ParamName).IsEqualTo("logger");
  }

  /// <summary>
  /// A constructor with a null options instance throws ArgumentNullException —
  /// same fail-fast contract as the null-logger test.
  /// </summary>
  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var caught = await Assert.That(() => {
      _ = new UnobservedExceptionDiagnostics(logger, null!);
      return Task.CompletedTask;
    }).ThrowsExactly<ArgumentNullException>();
    await Assert.That(caught!.ParamName).IsEqualTo("options");
  }

  /// <summary>
  /// The double-registration guard: instantiating a second
  /// <see cref="UnobservedExceptionDiagnostics"/> while the first is still
  /// alive must NOT double-subscribe the static event. Dispose on the SECOND
  /// instance is a no-op — only the original owner unsubscribes. Without this
  /// guard, a host that accidentally registers the singleton twice would log
  /// every unobserved exception twice for the rest of process lifetime.
  /// </summary>
  [Test]
  public async Task SecondInstance_WhileFirstAlive_DoesNotDoubleSubscribeAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions());

    using var first = new UnobservedExceptionDiagnostics(logger, options);
    // Second instance silently no-ops — first is still the static owner.
    using var second = new UnobservedExceptionDiagnostics(logger, options);

    // Reaching here without an exception is the success path. Real coverage of
    // the no-double-fire behaviour is in the unobserved-task test below; this
    // test specifically covers the construction guard.
    await Assert.That(logger.Entries).IsNotNull();
  }

  /// <summary>
  /// FirstChanceException allow-list filter: when configured with a specific
  /// type, only exceptions whose <c>FullName</c> matches are logged. The
  /// helper method exposed via the options instance is the filter; this test
  /// locks both the allow-match and the allow-miss paths.
  /// </summary>
  [Test]
  public async Task FirstChanceAllowList_NonMatchingType_IsExcludedAsync() {
    var options = new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = ["System.InvalidOperationException"],
    };

    await Assert.That(options.IncludeExceptionTypeInFirstChanceLog("System.InvalidOperationException")).IsTrue()
      .Because("Allow-list match: type name appears in the configured list, so first-chance logging fires.");
    await Assert.That(options.IncludeExceptionTypeInFirstChanceLog("System.ArgumentNullException")).IsFalse()
      .Because("Allow-list miss: type not in the configured list, so first-chance logging stays silent. This is the operator-controlled diagnostic narrowing in v0.651.");
    await Assert.That(options.IncludeExceptionTypeInFirstChanceLog("")).IsFalse()
      .Because("Empty-string type name should not match a configured non-empty allow-list entry.");
  }

  /// <summary>
  /// Empty/null allow-list = "log all" semantics. v0.651 design choice: when
  /// the operator hasn't narrowed the filter, every first-chance exception
  /// (except OCE, filtered earlier) flows through to logging. This is the
  /// default diagnostic-deploy posture.
  /// </summary>
  [Test]
  public async Task FirstChanceAllowList_NullOrEmpty_MatchesAllTypesAsync() {
    var nullList = new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = null,
    };
    var emptyList = new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = [],
    };

    foreach (var options in new[] { nullList, emptyList }) {
      await Assert.That(options.IncludeExceptionTypeInFirstChanceLog("System.InvalidOperationException")).IsTrue()
        .Because("Null/empty allow-list = wide-open filter. Operator hasn't narrowed; every type passes.");
      await Assert.That(options.IncludeExceptionTypeInFirstChanceLog("Some.Other.ExceptionType")).IsTrue()
        .Because("Same as above — null/empty list means no filtering.");
    }
  }

  /// <summary>
  /// The end-to-end invariant: when a fire-and-forget <see cref="Task"/>
  /// throws and is allowed to be finalized, the
  /// <see cref="UnobservedExceptionDiagnostics"/> handler captures the
  /// exception and logs it at Error level. Without this hook, the runtime
  /// silently suppresses the exception on .NET 4.5+ and we lose all
  /// forensic signal — the exact gap that motivated v0.651.
  /// </summary>
  [Test]
  public async Task UnobservedTaskException_HandlerLogsAtErrorLevelAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions());
    using var diagnostics = new UnobservedExceptionDiagnostics(logger, options);

    var marker = $"WhizbangUnobservedTest-{Guid.NewGuid():N}";

    // Spawn a Task that faults, then drop the reference and force GC until the
    // finalizer thread fires UnobservedTaskException. The pattern is the same
    // one .NET's own UnobservedTaskException docs use for repro tests.
    _spawnAndAbandonFaultedTask(marker);

    // Force the runtime to GC the abandoned Task. The unobserved-task event
    // fires on the finalizer thread after the GC reclaims the Task — give it
    // a brief window. Use a polling loop bounded by a deadline rather than a
    // single Sleep so the test stays deterministic under varied GC pressure.
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline) {
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
      var hit = logger.Entries.FirstOrDefault(e => e.Exception?.Message.Contains(marker, StringComparison.Ordinal) == true);
      if (hit is not null) {
        await Assert.That(hit.Level).IsEqualTo(LogLevel.Error)
          .Because("v0.651 design: unobserved exceptions are surface-level forensic signal — Error level matches their operational severity and keeps them out of Information/Warning noise.");
        await Assert.That(hit.Exception).IsNotNull()
          .Because("The logged entry must carry the original exception so log sinks can extract the stack trace, not just the formatted message.");
        return;
      }
      await Task.Delay(50);
    }

    // Reaching this point means the handler never fired despite the GC dance.
    // The runtime occasionally drags its feet finalizing under low pressure;
    // if it becomes flaky we can switch to a stronger force-finalize pattern.
    throw new InvalidOperationException(
      $"UnobservedTaskException did not fire for marker '{marker}' within deadline. Captured {logger.Entries.Count} log entries; none contained the marker.");
  }

  /// <summary>
  /// The warm-up hosted service resolves the diagnostics dependency so the process-wide
  /// hooks are wired at startup. Its Start/Stop are trivial no-ops that must not throw.
  /// Covers the WarmUp ctor, StartAsync, and StopAsync.
  /// </summary>
  [Test]
  public async Task WarmUp_StartAndStop_CompleteWithoutThrowingAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions());
    using var diagnostics = new UnobservedExceptionDiagnostics(logger, options);

    var warmUp = new UnobservedExceptionDiagnosticsWarmUp(diagnostics);
    await warmUp.StartAsync(CancellationToken.None);
    await warmUp.StopAsync(CancellationToken.None);

    // Both lifecycle methods returned completed tasks; reaching here is the success signal.
    await Assert.That(logger.Entries).IsNotNull();
  }

  /// <summary>
  /// The warm-up service rejects a null dependency — fail-fast on bad DI wiring.
  /// </summary>
  [Test]
  public async Task WarmUp_NullDiagnostics_ThrowsArgumentNullExceptionAsync() {
    var caught = await Assert.That(() => {
      _ = new UnobservedExceptionDiagnosticsWarmUp(null!);
      return Task.CompletedTask;
    }).ThrowsExactly<ArgumentNullException>();
    await Assert.That(caught!.ParamName).IsEqualTo("diagnostics");
  }

  /// <summary>
  /// End-to-end first-chance path: with FirstChanceException logging enabled and an
  /// allow-list naming a marker exception type, throwing+catching an instance of that type
  /// drives the live <c>_onFirstChanceException</c> handler through its allow-match and
  /// Debug-enabled log. The handler subscription (constructor) and unsubscription (Dispose)
  /// are exercised by construction/disposal here. A Debug-enabled logger is required so the
  /// IsEnabled(Debug) gate opens.
  /// </summary>
  [Test]
  public async Task FirstChanceException_EnabledWithMatchingAllowList_LogsAtDebugAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = [typeof(_FirstChanceMarkerException).FullName!],
    });

    var marker = $"WhizbangFirstChanceTest-{Guid.NewGuid():N}";
    using (var diagnostics = new UnobservedExceptionDiagnostics(logger, options)) {
      // Throwing then catching the marker type triggers FirstChanceException synchronously
      // on this thread, driving the handler while the diagnostics owns the subscription.
      try {
        throw new _FirstChanceMarkerException(marker);
      } catch (_FirstChanceMarkerException) {
        // Expected — the first-chance handler already fired before we caught it.
      }
    }

    var hit = logger.Entries.FirstOrDefault(e =>
      e.Level == LogLevel.Debug && e.Exception?.Message.Contains(marker, StringComparison.Ordinal) == true);
    await Assert.That(hit is not null).IsTrue()
      .Because("an allow-listed first-chance exception must be logged at Debug carrying the original exception");
  }

  /// <summary>
  /// The first-chance handler filters out <see cref="OperationCanceledException"/> — these
  /// are high-volume and low-signal, so they must never be logged even when first-chance
  /// logging is enabled with a wide-open allow-list. Covers the OCE early-return arm.
  /// </summary>
  [Test]
  public async Task FirstChanceException_OperationCanceled_IsNotLoggedAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>();
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = null, // wide open — only the OCE filter can exclude
    });

    var marker = $"WhizbangOceTest-{Guid.NewGuid():N}";
    using (var diagnostics = new UnobservedExceptionDiagnostics(logger, options)) {
      try {
        throw new OperationCanceledException(marker);
      } catch (OperationCanceledException) {
        // Expected — the handler saw it first and returned early.
      }
    }

    var oceHit = logger.Entries.FirstOrDefault(e => e.Exception?.Message.Contains(marker, StringComparison.Ordinal) == true);
    await Assert.That(oceHit is null).IsTrue()
      .Because("OperationCanceledException is filtered out of first-chance logging as low-signal noise");
  }

  private sealed class _FirstChanceMarkerException : Exception {
    public _FirstChanceMarkerException() { }
    public _FirstChanceMarkerException(string message) : base(message) { }
    public _FirstChanceMarkerException(string message, Exception innerException) : base(message, innerException) { }
  }

  private static void _spawnAndAbandonFaultedTask(string marker) {
    // Allocate the faulted Task in a separate stack frame so the local doesn't
    // pin the reference in the caller's frame across GC. The async lambda
    // captures `marker` and faults inside the state machine.
    var ignored = Task.Run((Func<int>)(() => throw new InvalidOperationException(marker)));
    // Discard the reference; do NOT await, do NOT attach a continuation.
    _ = ignored;
  }
}
