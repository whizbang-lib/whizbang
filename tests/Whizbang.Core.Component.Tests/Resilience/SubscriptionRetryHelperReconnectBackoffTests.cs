// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// A destination that keeps failing right after each successful subscribe (#1840): the reconnects back off across
/// disconnect cycles, growing from the last reconnect's delay by <see cref="SubscriptionResilienceOptions.BackoffMultiplier"/>
/// up to <see cref="SubscriptionResilienceOptions.MaxRetryDelay"/>, and start again from
/// <see cref="SubscriptionResilienceOptions.InitialRetryDelay"/> only once a subscription has stayed healthy for
/// <see cref="SubscriptionResilienceOptions.ReconnectBackoffResetAfter"/>.
/// </summary>
/// <remarks>
/// Every wait is taken on a fake clock that reports each timer as it is armed, so a test reads the delay a reconnect
/// chose from the reconnect's own timer, then advances the clock by exactly that much and waits for the change to
/// Healthy the reconnect records. Nothing depends on real time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Resilience/SubscriptionRetryHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Resilience/SubscriptionState.cs</code-under-test>
[Category("Resilience")]
public class SubscriptionRetryHelperReconnectBackoffTests {
  private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

  private static SubscriptionResilienceOptions _options() => new() {
    InitialRetryDelay = TimeSpan.FromSeconds(1),
    BackoffMultiplier = 2.0,
    MaxRetryDelay = TimeSpan.FromSeconds(8),
    ReconnectBackoffResetAfter = TimeSpan.FromSeconds(30),
  };

  [Test]
  public async Task SubscribeWithRetryAsync_DestinationDropsRightAfterEverySubscribe_EachReconnectWaitsLongerUpToTheCapAsync() {
    await using var harness = await BackoffHarness.StartAsync(_options());

    var waits = new List<TimeSpan>();
    for (var cycle = 0; cycle < 6; cycle++) {
      waits.Add(await harness.DropAndReconnectAsync());
    }

    await Assert.That(_seconds(waits)).IsEqualTo("1,2,4,8,8,8")
      .Because("each reconnect waits the previous reconnect's delay times the multiplier, capped at MaxRetryDelay");
    await Assert.That(harness.Transport.SubscribeCallCount).IsEqualTo(7)
      .Because("one subscribe to start and one per reconnect: the delays slow the reconnects, they never skip one");
    await Assert.That(harness.Logger.DisconnectWarnings[1]).Contains("2000ms")
      .Because("the disconnect warning says how long this reconnect waits, which is no longer always the initial delay");
  }

  [Test]
  public async Task SubscribeWithRetryAsync_SubscriptionStaysHealthyForTheResetPeriod_NextReconnectWaitsTheInitialDelayAgainAsync() {
    await using var harness = await BackoffHarness.StartAsync(_options());
    for (var cycle = 0; cycle < 3; cycle++) {
      await harness.DropAndReconnectAsync();
    }

    harness.Clock.Advance(TimeSpan.FromSeconds(30));
    var afterRecovery = await harness.DropAndReconnectAsync();
    var dropAgain = await harness.DropAndReconnectAsync();

    await Assert.That(afterRecovery).IsEqualTo(TimeSpan.FromSeconds(1))
      .Because("a subscription that stayed healthy for the reset period recovered; its drop is a fresh one-off drop");
    await Assert.That(dropAgain).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("the backoff starts again from the initial delay, not from where the old streak left it");
  }

  [Test]
  public async Task SubscribeWithRetryAsync_SubscriptionDropsJustBeforeTheResetPeriod_KeepsBackingOffAsync() {
    await using var harness = await BackoffHarness.StartAsync(_options());
    for (var cycle = 0; cycle < 3; cycle++) {
      await harness.DropAndReconnectAsync();
    }

    harness.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
    var wait = await harness.DropAndReconnectAsync();

    await Assert.That(wait).IsEqualTo(TimeSpan.FromSeconds(8))
      .Because("healthy for less than the reset period is still the same failing streak: 4s grows to 8s");
  }

  [Test]
  public async Task SubscribeWithRetryAsync_ResetPeriodIsZero_EveryReconnectWaitsTheInitialDelayAsync() {
    var options = _options();
    options.ReconnectBackoffResetAfter = TimeSpan.Zero;
    await using var harness = await BackoffHarness.StartAsync(options);

    var waits = new List<TimeSpan>();
    for (var cycle = 0; cycle < 3; cycle++) {
      waits.Add(await harness.DropAndReconnectAsync());
    }

    await Assert.That(_seconds(waits)).IsEqualTo("1,1,1")
      .Because("a zero reset period treats every disconnect as a fresh one");
  }

  private static string _seconds(IEnumerable<TimeSpan> waits) => string.Join(",", waits.Select(w => w.TotalSeconds));

  // ============================================================
  // Harness and test doubles
  // ============================================================

  /// <summary>
  /// Subscribes one destination through the helper on a <see cref="TimerRecordingClock"/>, and drives one
  /// disconnect-and-reconnect cycle at a time.
  /// </summary>
  private sealed class BackoffHarness : IAsyncDisposable {
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SubscriptionStatus> _statuses = Channel.CreateUnbounded<SubscriptionStatus>();
    private readonly SubscriptionResilienceOptions _resilience;

    private BackoffHarness(SubscriptionResilienceOptions options) {
      _resilience = options;
      State = new SubscriptionState(Transport.Destination) {
        StatusChanged = (_, _, status) => _statuses.Writer.TryWrite(status),
      };
    }

    public TimerRecordingClock Clock { get; } = new();
    public BackoffTransport Transport { get; } = new();
    public RecordingLogger Logger { get; } = new();
    public SubscriptionState State { get; }

    public static async Task<BackoffHarness> StartAsync(SubscriptionResilienceOptions options) {
      var harness = new BackoffHarness(options);
      await SubscriptionRetryHelper.SubscribeWithRetryAsync(
        harness.Transport, harness.Transport.Destination, (_, _) => Task.CompletedTask, new TransportBatchOptions(),
        harness.State, harness._resilience, harness.Logger, harness.Clock, harness._cts.Token);
      await harness._changedToHealthyAsync();
      return harness;
    }

    /// <summary>
    /// Drops the live subscription, reads the delay the reconnect armed, advances the clock by exactly that much, and
    /// waits for the reconnect to record Healthy. Returns the delay.
    /// </summary>
    public async Task<TimeSpan> DropAndReconnectAsync() {
      Transport.Subscriptions.Last().Disconnect(new InvalidOperationException("receiving failed right after subscribing"));
      var wait = await Clock.NextTimerAsync().WaitAsync(_guard);
      Clock.Advance(wait);
      await _changedToHealthyAsync().WaitAsync(_guard);
      return wait;
    }

    private async Task _changedToHealthyAsync() {
      while (await _statuses.Reader.ReadAsync() != SubscriptionStatus.Healthy) {
        // Recovering, recorded by the disconnect before the reconnect starts.
      }
    }

    public async ValueTask DisposeAsync() {
      await _cts.CancelAsync();
      _cts.Dispose();
    }
  }

  /// <summary>Returns a new <see cref="BackoffSubscription"/> on every subscribe.</summary>
  private sealed class BackoffTransport : ITransport {
    private int _subscribeCallCount;

    public TransportDestination Destination { get; } = new("backoff-topic", "backoff-key");
    public int SubscribeCallCount => Volatile.Read(ref _subscribeCallCount);
    public ConcurrentQueue<BackoffSubscription> Subscriptions { get; } = new();
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeCallCount);
      var subscription = new BackoffSubscription();
      Subscriptions.Enqueue(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
  }

  /// <summary>A subscription that records its unexpected disconnect and raises it, as a transport does.</summary>
  private sealed class BackoffSubscription : ISubscription {
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public SubscriptionDisconnectedEventArgs? Disconnection { get; private set; }
    public bool IsActive => true;

    public void Disconnect(Exception error) {
      var args = new SubscriptionDisconnectedEventArgs {
        Reason = "simulated disconnect",
        Exception = error,
        IsApplicationInitiated = false,
      };
      Disconnection ??= args;
      OnDisconnected?.Invoke(this, args);
    }

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }

  private sealed class RecordingLogger : ILogger {
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>The warnings the helper logs when it accepts a disconnect and starts a reconnect, in order.</summary>
    public IReadOnlyList<string> DisconnectWarnings => [.. _entries
      .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("disconnected", StringComparison.Ordinal))
      .Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      _entries.Enqueue((logLevel, formatter(state, exception)));
  }
}
