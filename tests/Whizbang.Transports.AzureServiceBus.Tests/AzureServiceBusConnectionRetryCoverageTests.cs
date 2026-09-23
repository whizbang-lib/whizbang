using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage-round targets for <see cref="AzureServiceBusConnectionRetry"/>: the indefinite-retry
/// heartbeat log, and the success path through the retry loop.
/// </summary>
/// <remarks>
/// The success path used to be unreachable from a unit test: everything past
/// <c>adminClient.GetNamespacePropertiesAsync(...)</c> needed a real management-plane round trip,
/// which the local emulator does not implement and which a unit test must not depend on. That
/// verification round trip is now an internal seam on the class
/// (<c>VerifyNamespaceReachableAsync</c>), so these tests drive the loop's failure-then-success
/// behavior offline while production still uses the real administration client.
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
  private sealed class StillRetryingLog : ILogger {
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
    var log = new StillRetryingLog();
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

  /// <summary>Records every log line so a test can assert which retry arm ran.</summary>
  private sealed class RecordingLog : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static AzureServiceBusOptions _fastRetryOptions() => new() {
    InitialRetryAttempts = 1,
    InitialRetryDelay = TimeSpan.FromMilliseconds(1),
    MaxRetryDelay = TimeSpan.FromMilliseconds(1),
    BackoffMultiplier = 1.0,
    RetryIndefinitely = true,
  };

  // Recovery is the whole point of retrying: a namespace that refuses once and answers next time
  // has to end with a usable client, not another retry. The "established after N attempts" line is
  // the only signal an operator gets that a pod which was stuck at startup is now connected, so it
  // has to carry the attempt number it actually took.
  [Test]
  public async Task CreateClientWithRetryAsync_WhenTheNamespaceAnswersOnTheSecondAttempt_ReturnsTheClientAndLogsRecoveryAsync(
      CancellationToken cancellationToken) {
    var log = new RecordingLog();
    var verifications = 0;
    var retry = new AzureServiceBusConnectionRetry(_fastRetryOptions(), log) {
      VerifyNamespaceReachableAsync = (_, _) => {
        verifications++;
        return verifications == 1
          ? Task.FromException(new Azure.RequestFailedException("namespace not answering yet"))
          : Task.CompletedTask;
      }
    };

    await using var client = await retry.CreateClientWithRetryAsync(UNREACHABLE_NAMESPACE, cancellationToken);

    await Assert.That(client).IsNotNull()
      .Because("a verified namespace must yield the client, not another retry");
    await Assert.That(verifications).IsEqualTo(2);
    await Assert.That(log.Entries.Count(e =>
        e.Level == LogLevel.Information &&
        e.Message.Contains("established after 2 attempts", StringComparison.Ordinal)))
      .IsEqualTo(1)
      .Because("the recovery line is an operator's only signal that a stalled startup connected, "
             + "and it must name the attempt it actually took");
  }

  // The first-attempt success is the normal case and must stay silent at Information level:
  // logging "established after 1 attempts" on every healthy start turns the recovery signal above
  // into background noise nobody reads.
  [Test]
  public async Task CreateClientWithRetryAsync_WhenTheNamespaceAnswersImmediately_ReturnsTheClientWithoutARecoveryLineAsync(
      CancellationToken cancellationToken) {
    var log = new RecordingLog();
    var verifications = 0;
    var retry = new AzureServiceBusConnectionRetry(_fastRetryOptions(), log) {
      VerifyNamespaceReachableAsync = (_, _) => {
        verifications++;
        return Task.CompletedTask;
      }
    };

    await using var client = await retry.CreateClientWithRetryAsync(UNREACHABLE_NAMESPACE, cancellationToken);

    await Assert.That(client).IsNotNull();
    await Assert.That(verifications).IsEqualTo(1);
    await Assert.That(log.Entries.Any(e => e.Message.Contains("established after", StringComparison.Ordinal)))
      .IsFalse()
      .Because("nothing was recovered from, so there is nothing to report at Information level");
  }
}
