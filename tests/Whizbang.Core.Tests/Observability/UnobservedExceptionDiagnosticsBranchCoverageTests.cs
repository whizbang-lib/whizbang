// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch backfill for <see cref="UnobservedExceptionDiagnostics.Dispose"/>: a second Dispose on an
/// already-disposed instance is a no-op and must not tear down a later owner's subscription.
/// </summary>
/// <remarks>
/// Shares the <c>[NotInParallel("WhizbangBackgroundServiceTests")]</c> key with the other
/// diagnostics tests: all of them manipulate the same process-wide registration slot and the same
/// static <see cref="AppDomain.FirstChanceException"/> event.
/// </remarks>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class UnobservedExceptionDiagnosticsBranchCoverageTests {

  private sealed class CapturingLogger<T> : ILogger<T> {
    private readonly List<Exception?> _exceptions = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_exceptions) { _exceptions.Add(exception); }
    }
    public bool SawMessage(string marker) {
      lock (_exceptions) {
        return _exceptions.Any(e => e?.Message.Contains(marker, StringComparison.Ordinal) == true);
      }
    }
  }

  public sealed class DisposeTwiceMarkerException : Exception {
    public DisposeTwiceMarkerException() { }
    public DisposeTwiceMarkerException(string message) : base(message) { }
    public DisposeTwiceMarkerException(string message, Exception innerException) : base(message, innerException) { }
  }

  [Test]
  public async Task Dispose_CalledAgainAfterANewOwnerRegistered_LeavesTheNewOwnerSubscribedAsync() {
    var options = Options.Create(new UnobservedExceptionDiagnosticsOptions {
      EnableFirstChanceExceptionLogging = true,
      FirstChanceExceptionTypeAllowList = null,
    });
    var firstLogger = new CapturingLogger<UnobservedExceptionDiagnostics>();
    var secondLogger = new CapturingLogger<UnobservedExceptionDiagnostics>();

    var first = new UnobservedExceptionDiagnostics(firstLogger, options);
    first.Dispose();
    using var second = new UnobservedExceptionDiagnostics(secondLogger, options);

    // The repeat Dispose on the stale instance: must return without touching the slot that the
    // second instance now owns.
    first.Dispose();

    var marker = $"WhizbangDisposeTwiceTest-{Guid.NewGuid():N}";
    try {
      throw new DisposeTwiceMarkerException(marker);
    } catch (DisposeTwiceMarkerException) {
      // Expected: the first-chance handler has already run by the time this catch is entered.
    }

    await Assert.That(secondLogger.SawMessage(marker)).IsTrue()
      .Because("a repeated Dispose of a stale instance must not unsubscribe the live owner's handlers");
    await Assert.That(firstLogger.SawMessage(marker)).IsFalse()
      .Because("the disposed instance's handler was removed by its first Dispose");
  }
}
