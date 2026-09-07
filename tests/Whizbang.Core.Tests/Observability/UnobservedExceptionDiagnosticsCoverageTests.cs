using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Coverage round 23 tail: the two remaining early returns in
/// <c>UnobservedExceptionDiagnostics._onFirstChanceException</c> — the allow-list MISS arm and the
/// Debug-not-enabled arm. <c>UnobservedExceptionDiagnosticsTests.cs</c> already covers the OCE
/// filter and the allow-list MATCH-and-log path, but nothing drives a real
/// <see cref="AppDomain.FirstChanceException"/> through a non-matching allow-list or a
/// Debug-disabled logger.
/// </summary>
/// <remarks>
/// Reuses the established technique from <c>UnobservedExceptionDiagnosticsTests.cs</c>: a unique
/// marker string per test plus a real throw/catch (FirstChanceException fires synchronously on
/// this thread), so a concurrent unrelated exception elsewhere in the process cannot produce a
/// false pass. Shares that file's <c>[NotInParallel("WhizbangBackgroundServiceTests")]</c>
/// constraint key because both manipulate the same process-wide static registration slot and the
/// same static <see cref="AppDomain.FirstChanceException"/> event.
/// </remarks>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class UnobservedExceptionDiagnosticsCoverageTests {

  private sealed record _LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class _CapturingLogger<T>(bool debugEnabled) : ILogger<T> {
    public List<_LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug || debugEnabled;

    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new _LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class _NullScope : IDisposable {
      public static readonly _NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class _FirstChanceCoverageMarkerException : Exception {
    public _FirstChanceCoverageMarkerException() { }
    public _FirstChanceCoverageMarkerException(string message) : base(message) { }
    public _FirstChanceCoverageMarkerException(string message, Exception innerException) : base(message, innerException) { }
  }

  /// <summary>
  /// Operator impact: the allow-list exists so a diagnostic deploy can narrow first-chance
  /// logging to ONE suspected exception type without drowning in unrelated noise. If a
  /// non-matching type slipped through and got logged anyway, that narrowing would be a no-op and
  /// the deploy would be exactly as noisy as the wide-open default.
  /// </summary>
  [Test]
  public async Task FirstChanceException_NonAllowListedType_IsNotLoggedAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>(debugEnabled: true);
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = ["System.InvalidOperationException"],
    });

    var marker = $"WhizbangAllowListMissTest-{Guid.NewGuid():N}";
    using (var diagnostics = new UnobservedExceptionDiagnostics(logger, options)) {
      try {
        throw new _FirstChanceCoverageMarkerException(marker);
      } catch (_FirstChanceCoverageMarkerException) {
        // Expected — the first-chance handler already ran (and skipped logging) before we caught it.
      }
    }

    var hit = logger.Entries.FirstOrDefault(e => e.Exception?.Message.Contains(marker, StringComparison.Ordinal) == true);
    await Assert.That(hit is null).IsTrue()
      .Because("a type absent from a configured allow-list must be silently skipped, not logged — "
             + "otherwise the allow-list narrowing is decorative");
  }

  /// <summary>
  /// Operator impact: this final gate exists so first-chance subscription can stay on (cheap) even
  /// when the logger's minimum level is above Debug — a host running at Information in production
  /// must not pay for formatting first-chance log lines it will immediately discard, and must not
  /// emit any despite the feature being enabled.
  /// </summary>
  [Test]
  public async Task FirstChanceException_DebugDisabled_IsNotLoggedAsync() {
    var logger = new _CapturingLogger<UnobservedExceptionDiagnostics>(debugEnabled: false);
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = null, // wide open — only the Debug gate can exclude this
    });

    var marker = $"WhizbangDebugDisabledTest-{Guid.NewGuid():N}";
    using (var diagnostics = new UnobservedExceptionDiagnostics(logger, options)) {
      try {
        throw new _FirstChanceCoverageMarkerException(marker);
      } catch (_FirstChanceCoverageMarkerException) {
        // Expected — the first-chance handler already ran (and skipped logging) before we caught it.
      }
    }

    var hit = logger.Entries.FirstOrDefault(e => e.Exception?.Message.Contains(marker, StringComparison.Ordinal) == true);
    await Assert.That(hit is null).IsTrue()
      .Because("with Debug disabled on the logger, the handler must never format or emit the "
             + "first-chance log line, even though logging is enabled and the allow-list is wide open");
  }
}
