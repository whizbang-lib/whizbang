using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Coverage gaps left by <c>RabbitMQSubscriptionTests</c>: the shutdown handler's own
/// disposed-guard (only reachable when a shutdown event is still in flight after
/// <c>Dispose()</c> has already unsubscribed it — a real broker race, reproduced here
/// deterministically via <see cref="FakeChannel.SuppressChannelShutdownUnsubscribe"/>), and
/// the fire-and-forget dispose path's catch arm when the channel itself throws while closing.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQSubscription.cs</code-under-test>
public class RabbitMQSubscriptionCoverageTests {

  private const string QUEUE_NAME = "test-subscription-queue";

  /// <summary>
  /// Production risk if this regresses: a shutdown notification that arrives while
  /// <c>Dispose()</c> is mid-flight would fire <c>OnDisconnected</c> on an already-torn-down
  /// subscription — a caller reacting to it would attempt to reconnect or otherwise act on a
  /// resource that no longer exists.
  /// </summary>
  [Test]
  public async Task ChannelShutdown_ArrivingAfterDisposedFlagIsSet_ReturnsWithoutFiringOnDisconnectedAsync() {
    var channel = new FakeChannel { SuppressChannelShutdownUnsubscribe = true };
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);

    var fired = false;
    subscription.OnDisconnected += (_, _) => fired = true;

    // Dispose() sets the disposed flag synchronously before its fire-and-forget cleanup task
    // even starts, so the flag is already true here. The fake's unsubscription is suppressed,
    // so the handler is still wired — reproducing the narrow window where a broker shutdown
    // notification is delivered to a subscription that has already begun disposing.
    subscription.Dispose();

    await channel.SimulateShutdownAsync(ShutdownInitiator.Peer, "late shutdown, still wired");

    await Assert.That(fired).IsFalse()
      .Because("the handler's own disposed-guard must short-circuit even when the channel "
             + "still has it wired");
  }

  /// <summary>
  /// Production risk if this regresses: a channel that throws while being disposed would go
  /// completely unnoticed — the fire-and-forget dispose path swallows exceptions by design, but
  /// only the catch-and-log arm makes that failure observable to an operator instead of a
  /// silently leaked or half-closed channel.
  /// </summary>
  [Test]
  public async Task Dispose_WhenChannelDisposeThrows_LogsTheErrorInsteadOfLosingItAsync() {
    var channel = new FakeChannel {
      ExceptionToThrowOnDispose = new InvalidOperationException("channel refused to close"),
    };
    var logger = new RecordingLogger { SignalOnContains = "Error disposing subscription" };
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, consumerTag: null, logger);

    subscription.Dispose();

    // Deterministic completion signal for the fire-and-forget Task.Run body — no polling.
    await logger.Signal.Task;

    await Assert.That(logger.HasMessageContaining("Error disposing subscription")).IsTrue();
    await Assert.That(channel.IsDisposed).IsFalse()
      .Because("Dispose() throws before FakeChannel marks itself disposed — the catch arm must "
             + "still run rather than propagate the exception out of the fire-and-forget task");
  }

  /// <summary>Minimal recording logger, local to this file's dispose-catch test.</summary>
  private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger {
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();
    public string? SignalOnContains { get; set; }
    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      lock (_gate) {
        _entries.Add(message);
      }
      if (SignalOnContains is not null && message.Contains(SignalOnContains, StringComparison.Ordinal)) {
        Signal.TrySetResult();
      }
    }

    public bool HasMessageContaining(string fragment) {
      lock (_gate) {
        return _entries.Exists(e => e.Contains(fragment, StringComparison.Ordinal));
      }
    }

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }
}
