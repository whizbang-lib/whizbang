using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for <see cref="RabbitMQSubscription"/> lifecycle: pause/resume idempotency and
/// debug logging, channel-shutdown → OnDisconnected fan-out (including the disposed and
/// application-initiated short-circuits), and the fire-and-forget dispose path with its
/// consumer-cancel and channel-dispose debug logs.
/// </summary>
public class RabbitMQSubscriptionTests {

  private const string QUEUE_NAME = "test-subscription-queue";
  private const string CONSUMER_TAG = "test-consumer-tag";

  /// <summary>
  /// Recording logger with an optional signal fired when a message containing
  /// <see cref="SignalOnContains"/> is logged. Lets dispose tests await the fire-and-forget
  /// Task.Run body without polling or Task.Delay.
  /// </summary>
  private sealed class RecordingLogger : ILogger {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _gate = new();
    public string? SignalOnContains { get; set; }
    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      lock (_gate) {
        _entries.Add((logLevel, message));
      }
      if (SignalOnContains is not null && message.Contains(SignalOnContains, StringComparison.Ordinal)) {
        Signal.TrySetResult();
      }
    }

    public void ClearEntries() {
      lock (_gate) {
        _entries.Clear();
      }
    }

    public bool HasMessageContaining(string fragment) {
      lock (_gate) {
        return _entries.Exists(e => e.Message.Contains(fragment, StringComparison.Ordinal));
      }
    }

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  [Test]
  public async Task Constructor_WithNullChannel_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new RabbitMQSubscription(null!, QUEUE_NAME))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullQueueName_ThrowsArgumentNullExceptionAsync() {
    using var channel = new FakeChannel();
    await Assert.That(() => new RabbitMQSubscription(channel, null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task IsActive_WhenFreshlyConstructed_IsTrueAsync() {
    using var channel = new FakeChannel();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);

    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task PauseAsync_WhenActive_SetsInactiveAndLogsAsync() {
    using var channel = new FakeChannel();
    var logger = new RecordingLogger();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG, logger);

    await subscription.PauseAsync();

    await Assert.That(subscription.IsActive).IsFalse();
    await Assert.That(logger.HasMessageContaining("Paused subscription")).IsTrue();
  }

  [Test]
  public async Task PauseAsync_WhenAlreadyPaused_ShortCircuitsAndLogsSkipAsync() {
    using var channel = new FakeChannel();
    var logger = new RecordingLogger();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG, logger);

    await subscription.PauseAsync();
    logger.ClearEntries();

    // Second pause hits the already-paused guard → debug "already paused, skipping" log.
    await subscription.PauseAsync();

    await Assert.That(subscription.IsActive).IsFalse();
    await Assert.That(logger.HasMessageContaining("already paused")).IsTrue();
  }

  [Test]
  public async Task PauseAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    using var channel = new FakeChannel();
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);
    subscription.Dispose();

    await Assert.That(async () => await subscription.PauseAsync())
      .Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task ResumeAsync_WhenPaused_SetsActiveAndLogsAsync() {
    using var channel = new FakeChannel();
    var logger = new RecordingLogger();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG, logger);

    await subscription.PauseAsync();
    logger.ClearEntries();

    await subscription.ResumeAsync();

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(logger.HasMessageContaining("Resumed subscription")).IsTrue();
  }

  [Test]
  public async Task ResumeAsync_WhenAlreadyActive_ShortCircuitsAndLogsSkipAsync() {
    using var channel = new FakeChannel();
    var logger = new RecordingLogger();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG, logger);

    // Fresh subscription is already active → resume hits the already-active guard.
    await subscription.ResumeAsync();

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(logger.HasMessageContaining("already active")).IsTrue();
  }

  [Test]
  public async Task ResumeAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    using var channel = new FakeChannel();
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);
    subscription.Dispose();

    await Assert.That(async () => await subscription.ResumeAsync())
      .Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task ChannelShutdown_WhenNotApplicationInitiated_FiresOnDisconnectedAndMarksInactiveAsync() {
    using var channel = new FakeChannel();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);

    var tcs = new TaskCompletionSource<SubscriptionDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscription.OnDisconnected += (_, args) => tcs.TrySetResult(args);

    await channel.SimulateShutdownAsync(ShutdownInitiator.Peer, "connection lost");

    var received = await tcs.Task;
    await Assert.That(received.IsApplicationInitiated).IsFalse();
    await Assert.That(received.Reason).IsEqualTo("connection lost");
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task ChannelShutdown_WhenApplicationInitiated_DoesNotFireOnDisconnectedButMarksInactiveAsync() {
    using var channel = new FakeChannel();
    using var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);

    var fired = false;
    subscription.OnDisconnected += (_, _) => fired = true;

    await channel.SimulateShutdownAsync(ShutdownInitiator.Application, "graceful stop");

    // Application-initiated shutdowns must NOT trigger reconnection, but still mark inactive.
    await Assert.That(fired).IsFalse();
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task ChannelShutdown_AfterDispose_DoesNotFireOnDisconnectedAsync() {
    var channel = new FakeChannel();
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME);

    var fired = false;
    subscription.OnDisconnected += (_, _) => fired = true;

    subscription.Dispose();

    // Dispose detaches the channel-shutdown handler, so a subsequent broker shutdown must not
    // reach the subscription and must not fire OnDisconnected.
    await channel.SimulateShutdownAsync(ShutdownInitiator.Peer, "late shutdown");

    await Assert.That(fired).IsFalse();
  }

  [Test]
  public async Task Dispose_WithConsumerTagAndDebugLogger_CancelsConsumerAndLogsAsync() {
    var channel = new FakeChannel();
    var logger = new RecordingLogger { SignalOnContains = "Disposed channel" };
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG, logger);

    subscription.Dispose();

    // Dispose spawns a fire-and-forget Task.Run. Await the signal fired when the "Disposed
    // channel" debug log runs — no polling, no Task.Delay.
    await logger.Signal.Task;

    await Assert.That(channel.BasicCancelAsyncCalled).IsTrue();
    await Assert.That(channel.IsDisposed).IsTrue();
    await Assert.That(logger.HasMessageContaining("Cancelled consumer")).IsTrue();
    await Assert.That(logger.HasMessageContaining("Disposed channel")).IsTrue();
  }

  [Test]
  public async Task Dispose_WithoutConsumerTag_DisposesChannelWithoutCancelAsync() {
    var channel = new FakeChannel();
    var logger = new RecordingLogger { SignalOnContains = "Disposed channel" };
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, consumerTag: null, logger);

    subscription.Dispose();
    await logger.Signal.Task;

    // No consumer tag → BasicCancelAsync is skipped, but the channel is still disposed.
    await Assert.That(channel.BasicCancelAsyncCalled).IsFalse();
    await Assert.That(channel.IsDisposed).IsTrue();
  }

  [Test]
  public async Task Dispose_WhenCalledTwice_SecondCallIsNoOpAsync() {
    var channel = new FakeChannel();
    var logger = new RecordingLogger { SignalOnContains = "Disposed channel" };
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG, logger);

    subscription.Dispose();
    await logger.Signal.Task;

    // Second dispose must hit the _disposed guard and return immediately — no throw.
    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task IsActive_AfterDispose_IsFalseAsync() {
    var channel = new FakeChannel();
    var subscription = new RabbitMQSubscription(channel, QUEUE_NAME, CONSUMER_TAG);

    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }
}
