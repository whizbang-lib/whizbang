using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage-round-23 targets for <see cref="AzureServiceBusConnectionRetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only source lines 104-105 (the "still retrying" heartbeat log inside
/// <c>_handleRetryOrRethrow</c>'s indefinite-retry branch) are exercised here. The other five
/// target lines for this class — 76, 77, 78 (the "connection established after N attempts" log),
/// 80 (<c>return client;</c>), and 87 (the method's closing brace, which the compiler's async
/// state-machine epilogue only reaches via that same successful return) are NOT reachable from a
/// unit test and are not attempted here.
/// </para>
/// <para>
/// All five sit downstream of <c>adminClient.GetNamespacePropertiesAsync(...)</c> actually
/// succeeding — a real Azure Service Bus management-plane round trip that the local emulator does
/// not implement, with no seam in this class to substitute a fake admin client. This is
/// previously-recorded residue (entries AF and AP), not a new gap: pointing this method at a
/// namespace that doesn't exist can only ever take the failure path (already covered by
/// <c>AzureServiceBusConnectionRetryTests</c>), and pointing it at a real namespace would make
/// this suite depend on live Azure infrastructure and network conditions — exactly what a unit
/// test must not do. Reaching lines 76-80/87 requires either a real namespace in CI or a
/// constructor seam for the admin client, neither of which exists today.
/// </para>
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
/// <tests>Whizbang.Transports.AzureServiceBus/AzureServiceBusConnectionRetry.cs:*</tests>
public class AzureServiceBusConnectionRetryCoverageTests {

  /// <summary>
  /// A well-formed connection string whose endpoint refuses immediately — the same fail-fast
  /// pattern <c>AzureServiceBusConnectionRetryTests</c> uses: no broker, no DNS, and no network
  /// timeout to wait out, so the retry loop's catch path runs deterministically and fast.
  /// </summary>
  private const string UNREACHABLE_NAMESPACE =
    "Endpoint=sb://localhost:1;SharedAccessKeyName=probe;SharedAccessKey=cHJvYmVrZXk=";

  /// <summary>Counts connection attempts and records the attempt number of every "still retrying" log line.</summary>
  private sealed class _stillRetryingLog : ILogger {
    private int _attempts;

    public List<int> StillRetryingAtAttempt { get; } = [];
    public Action<int>? OnAttempt { get; set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      if (message.Contains("Attempting", StringComparison.Ordinal)) {
        // Increment OUTSIDE the null-conditional: `OnAttempt?.Invoke(Interlocked.Increment(...))`
        // never evaluates its argument when no callback is set, so the count silently stays zero.
        var attempt = Interlocked.Increment(ref _attempts);
        OnAttempt?.Invoke(attempt);
      } else if (message.Contains("Continuing to retry", StringComparison.Ordinal)) {
        // Same call stack as the "Attempting" log for this iteration (no concurrent iterations),
        // so the last-recorded attempt number is exactly the one _handleRetryOrRethrow was given.
        StillRetryingAtAttempt.Add(Volatile.Read(ref _attempts));
      }
    }
  }

  // An indefinitely-retrying connection is meant to run quietly once past its initial warning
  // budget, but "quietly" must not mean "silently forever": an operator watching the log needs a
  // periodic heartbeat proving a pod stuck at startup is still trying, not wedged. If the
  // attempt % 10 gate regressed to never fire, a broker outage that outlasts a few seconds would
  // stop producing any signal at all past the initial attempts, and a pod retrying forever would
  // look identical to a pod that silently died. If it fired on every attempt instead, the
  // heartbeat this line exists to keep rare would flood the log during a real outage.
  [Test]
  [Timeout(180000)]
  public async Task WhenRetryingIndefinitely_LogsStillRetryingOnlyOnTheTenthAttemptAsync(
      CancellationToken cancellationToken) {
    // Arrange
    var log = new _stillRetryingLog();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    log.OnAttempt = attempt => {
      // Let attempt 10 -- the modulo-10 gate this test targets -- complete its full retry cycle
      // (catch, log, delay) undisturbed. Canceling only once attempt 11 starts means the outcome
      // of attempt 10 is already recorded before shutdown can race it.
      if (attempt >= 11) {
        cts.Cancel();
      }
    };
    var options = new AzureServiceBusOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      MaxRetryDelay = TimeSpan.FromMilliseconds(2),
      RetryIndefinitely = true,
    };
    var retry = new AzureServiceBusConnectionRetry(options, log);

    // Act & Assert -- shutdown is still the only thing that ends an indefinite retry.
    await Assert.That(async () =>
        await retry.CreateClientWithRetryAsync(UNREACHABLE_NAMESPACE, cts.Token))
      .Throws<OperationCanceledException>()
      .Because("canceling is the only way an indefinite retry loop ends; a bad connection "
             + "string alone must never surface as anything but the eventual cancellation here");

    await Assert.That(log.StillRetryingAtAttempt.Contains(10)).IsTrue()
      .Because("the tenth attempt is exactly where attempt % 10 == 0 first holds true after the "
             + "initial warning budget, so the heartbeat must have fired there");

    await Assert.That(log.StillRetryingAtAttempt.Any(attempt => attempt < 10)).IsFalse()
      .Because("firing before the tenth attempt means the modulo gate is not filtering at all, "
             + "which is the log-spam failure mode the gate exists to prevent");
  }
}
