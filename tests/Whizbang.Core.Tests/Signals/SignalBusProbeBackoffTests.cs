// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Locks how the wire-route self-test recovers from a failed probe. A transient first-probe
/// timeout (a cold start with a busy database) must not hold the signal-bus component Degraded for
/// the whole re-probe interval: a failed probe is retried on a short backoff before the loop returns
/// to its normal cadence, and a real wire signal arriving after the failure clears it at once. A
/// route that is genuinely dead still reports Degraded and keeps saying so.
/// </summary>
/// <remarks>
/// Time is driven by hand. The probe loop arms every wait (the probe timeout and the delay before the
/// next probe) through the injected <see cref="TimeProvider"/>, so each test waits for the timer it
/// expects to be armed, by its due time, and only then advances the clock. Nothing waits on the wall
/// clock.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Signals/SignalBusHostedService.cs</code-under-test>
public class SignalBusProbeBackoffTests {
  private const int PROBE_TIMEOUT_MS = 1_000;
  private const int RE_PROBE_INTERVAL_MS = 300_000;

  /// <summary>Drops its first <c>dropCount</c> probes, then loops every later one back.</summary>
  private sealed class DropsFirstProbesTransport(int dropCount) : ISignalTransport {
    private ISignalSink? _sink;
    private int _published;

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      _sink = sink;
      return Task.CompletedTask;
    }

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal =>
      Interlocked.Increment(ref _published) > dropCount && _sink is not null
        ? _sink.ReceiveAsync(signal, cancellationToken)
        : ValueTask.CompletedTask;
  }

  /// <summary>Delivers nothing, ever: a route that is really down.</summary>
  private sealed class DeadTransport : ISignalTransport {
    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  /// <summary>A fake clock that reports each timer the code under test arms, with its due time.</summary>
  private sealed class ArmedTimerClock : FakeTimeProvider {
    private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

    public ArmedTimerClock() : base(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      _armed.Writer.TryWrite(dueTime);
      return timer;
    }

    /// <summary>
    /// Completes once a timer with <paramref name="due"/> has been armed, skipping earlier ones
    /// (a probe wait that completed before its timeout arms and discards one).
    /// </summary>
    public async Task WaitForArmedAsync(TimeSpan due, CancellationToken cancellationToken) {
      TimeSpan armed;
      do {
        armed = await _armed.Reader.ReadAsync(cancellationToken);
      } while (armed != due);
    }

    /// <summary>Waits for the loop to arm <paramref name="due"/>, then advances past it.</summary>
    public async Task FireAsync(TimeSpan due, CancellationToken cancellationToken) {
      await WaitForArmedAsync(due, cancellationToken);
      Advance(due);
    }
  }

  private sealed record Harness(
    SignalBusHostedService Service,
    SignalBusLivenessState State,
    ArmedTimerClock Clock,
    CapturingLogger<SignalBusHostedService> Logger,
    ServiceProvider Provider) : IAsyncDisposable {
    private readonly List<bool> _verdicts = [];
    private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _waiters = [];

    public void Track() => State.ProbeCompleted += verdict => {
      List<TaskCompletionSource<bool>> ready = [];
      lock (_verdicts) {
        _verdicts.Add(verdict);
        foreach (var waiter in _waiters.Where(w => w.Count == _verdicts.Count).ToList()) {
          ready.Add(waiter.Signal);
          _waiters.Remove(waiter);
        }
      }
      ready.ForEach(s => s.TrySetResult(verdict));
    };

    /// <summary>Completes with the verdict of the <paramref name="count"/>th probe (1-based).</summary>
    public Task<bool> VerdictAsync(int count) {
      lock (_verdicts) {
        if (_verdicts.Count >= count) {
          return Task.FromResult(_verdicts[count - 1]);
        }
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((count, signal));
        return signal.Task;
      }
    }

    public async ValueTask DisposeAsync() {
      await Service.StopAsync(CancellationToken.None);
      Service.Dispose();
      await Provider.DisposeAsync();
    }
  }

  private static async Task<Harness> _startAsync(ISignalTransport transport, Action<SignalBusOptions>? configure = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddSingleton(transport);
    var provider = services.BuildServiceProvider();

    var options = new SignalBusOptions {
      ProbeTimeoutMilliseconds = PROBE_TIMEOUT_MS,
      ReProbeIntervalMilliseconds = RE_PROBE_INTERVAL_MS,
    };
    configure?.Invoke(options);
    var clock = new ArmedTimerClock();
    var logger = new CapturingLogger<SignalBusHostedService>();
    var state = provider.GetRequiredService<SignalBusLivenessState>();
    // Only the transport under test is probed: the in-memory loopback registered by default would
    // arm (and discard) probe timers of its own and blur the sequence the test drives.
    var service = new SignalBusHostedService(
      provider.GetRequiredService<SignalBus>(), state, [transport], logger,
      provider.GetRequiredService<IServiceInstanceProvider>(), Options.Create(options), clock);
    var harness = new Harness(service, state, clock, logger, provider);
    harness.Track();
    await service.StartAsync(CancellationToken.None);
    return harness;
  }

  private static TimeSpan _ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

  [Test]
  [Timeout(30_000)]
  public async Task TransientFirstProbeFailure_ReturnsToOperationalWithinTheBackoffAsync(CancellationToken cancellationToken) {
    await using var harness = await _startAsync(new DropsFirstProbesTransport(dropCount: 1));
    var started = harness.Clock.GetUtcNow();

    await harness.Clock.FireAsync(_ms(PROBE_TIMEOUT_MS), cancellationToken);
    await Assert.That(await harness.VerdictAsync(1).WaitAsync(cancellationToken)).IsFalse();
    await Assert.That(harness.State.Report().State).IsEqualTo(ComponentState.Degraded);

    await harness.Clock.FireAsync(_ms(SignalBusOptions.DefaultFailedProbeRetryDelaysMilliseconds[0]), cancellationToken);
    await Assert.That(await harness.VerdictAsync(2).WaitAsync(cancellationToken)).IsTrue()
      .Because("the retry after the first backoff step reaches a route that was only slow");

    await Assert.That(harness.State.Report().State).IsEqualTo(ComponentState.Operational);
    await Assert.That(harness.Clock.GetUtcNow() - started).IsLessThan(_ms(RE_PROBE_INTERVAL_MS))
      .Because("a transient failure clears within the backoff, not after the full re-probe interval");

    await harness.Clock.WaitForArmedAsync(_ms(RE_PROBE_INTERVAL_MS), cancellationToken);
    // Reaching here is the assertion: once a probe passes, the loop is back on its normal cadence.

    var logs = harness.Logger.Snapshot();
    await Assert.That(logs.Any(l => l.Level == LogLevel.Error)).IsFalse()
      .Because("a single miss that the next retry clears is not an error an operator should chase");
    var failure = logs.Single(l => l.Level == LogLevel.Warning);
    await Assert.That(failure.Message).Contains("re-probing in 5000ms");
    await Assert.That(failure.Message).DoesNotContain("running on polling fallback")
      .Because("the probe verdict feeds the health component; it does not switch any pump to polling");
    await Assert.That(logs.Any(l => l.Level == LogLevel.Information && l.Message.Contains("recovered after 1 failed probe"))).IsTrue();
  }

  [Test]
  [Timeout(30_000)]
  public async Task DeadTransport_StaysDegraded_BacksOffThenReturnsToTheNormalIntervalAsync(CancellationToken cancellationToken) {
    await using var harness = await _startAsync(new DeadTransport());
    var backoff = SignalBusOptions.DefaultFailedProbeRetryDelaysMilliseconds;

    var probe = 0;
    await harness.Clock.FireAsync(_ms(PROBE_TIMEOUT_MS), cancellationToken);
    await Assert.That(await harness.VerdictAsync(++probe).WaitAsync(cancellationToken)).IsFalse();
    foreach (var delay in backoff) {
      await harness.Clock.FireAsync(_ms(delay), cancellationToken);
      await harness.Clock.FireAsync(_ms(PROBE_TIMEOUT_MS), cancellationToken);
      await Assert.That(await harness.VerdictAsync(++probe).WaitAsync(cancellationToken)).IsFalse();
      await Assert.That(harness.State.Report().State).IsEqualTo(ComponentState.Degraded);
    }

    // Backoff exhausted: the next probe waits the normal interval, and still fails.
    await harness.Clock.FireAsync(_ms(RE_PROBE_INTERVAL_MS), cancellationToken);
    await harness.Clock.FireAsync(_ms(PROBE_TIMEOUT_MS), cancellationToken);
    await Assert.That(await harness.VerdictAsync(++probe).WaitAsync(cancellationToken)).IsFalse();

    var report = harness.State.Report();
    await Assert.That(report.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(report.Detail).Contains("DeadTransport");
    await Assert.That(report.Detail).Contains("until a probe passes or a wire signal arrives");

    var failures = harness.Logger.Snapshot().Where(l => l.Level >= LogLevel.Warning).ToList();
    await Assert.That(failures.Count).IsEqualTo(probe)
      .Because("every failed probe is logged, so a dead route keeps saying so");
    await Assert.That(failures.Take(backoff.Count).All(l => l.Level == LogLevel.Warning)).IsTrue()
      .Because("while retries remain the failure may still be transient");
    await Assert.That(failures.Skip(backoff.Count).All(l => l.Level == LogLevel.Error)).IsTrue()
      .Because("once the backoff is exhausted the route is reported as failed");
    await Assert.That(failures[^1].Message).Contains($"{probe} consecutive");
  }

  [Test]
  [Timeout(30_000)]
  public async Task FirstProbeTimeout_GivesTheStartupProbeLongerThanLaterOnesAsync(CancellationToken cancellationToken) {
    const int FIRST_PROBE_TIMEOUT_MS = 20_000;
    await using var harness = await _startAsync(new DeadTransport(),
      o => o.FirstProbeTimeoutMilliseconds = FIRST_PROBE_TIMEOUT_MS);

    await harness.Clock.FireAsync(_ms(FIRST_PROBE_TIMEOUT_MS), cancellationToken);
    await Assert.That(await harness.VerdictAsync(1).WaitAsync(cancellationToken)).IsFalse();

    await harness.Clock.FireAsync(_ms(SignalBusOptions.DefaultFailedProbeRetryDelaysMilliseconds[0]), cancellationToken);
    await harness.Clock.FireAsync(_ms(PROBE_TIMEOUT_MS), cancellationToken);
    await Assert.That(await harness.VerdictAsync(2).WaitAsync(cancellationToken)).IsFalse()
      .Because("only the startup probe gets the grace; the retry waits the ordinary timeout");
  }

  [Test]
  [Timeout(30_000)]
  public async Task EmptyRetrySchedule_FailedProbeWaitsTheNormalIntervalAsync(CancellationToken cancellationToken) {
    await using var harness = await _startAsync(new DeadTransport(),
      o => o.FailedProbeRetryDelaysMilliseconds = []);

    await harness.Clock.FireAsync(_ms(PROBE_TIMEOUT_MS), cancellationToken);
    await Assert.That(await harness.VerdictAsync(1).WaitAsync(cancellationToken)).IsFalse();
    await harness.Clock.WaitForArmedAsync(_ms(RE_PROBE_INTERVAL_MS), cancellationToken);

    await Assert.That(harness.Logger.Snapshot().Single(l => l.Level >= LogLevel.Warning).Level).IsEqualTo(LogLevel.Error)
      .Because("with no retries configured the first failure is already the final word until the next interval");
  }

  [Test]
  public async Task WireSignalAfterAFailedProbe_ClearsTheFailureAsync() {
    var state = new SignalBusLivenessState();
    var probedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 5, TimeSpan.Zero);
    state.MarkProbeResult(success: false, at: probedAt, failedTransport: "PostgresSignalTransport");

    state.MarkWireSignalReceived(probedAt - TimeSpan.FromSeconds(1));
    await Assert.That(state.WireRouteVerified).IsEqualTo(false)
      .Because("a signal from before the failed probe says nothing about the route since");

    state.MarkWireSignalReceived(probedAt);
    await Assert.That(state.WireRouteVerified).IsEqualTo(false)
      .Because("only a signal strictly after the probe is evidence the route works now");

    state.MarkWireSignalReceived(probedAt + TimeSpan.FromSeconds(1));
    await Assert.That(state.WireRouteVerified).IsEqualTo(true)
      .Because("a real wire signal arriving after the failed probe proves the route delivers");
    var report = state.Report();
    await Assert.That(report.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(report.Detail).IsNull();
  }

  [Test]
  public async Task WireSignalBeforeAnyProbe_DoesNotVouchForTheRouteAsync() {
    var state = new SignalBusLivenessState();

    state.MarkWireSignalReceived(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    await Assert.That(state.WireRouteVerified).IsNull()
      .Because("the verdict belongs to the startup self-test; a signal only clears a failure it already reported");
  }

  [Test]
  public async Task FailedProbeReport_DescribesTheVerdict_NotAPollingSwitchAsync() {
    var state = new SignalBusLivenessState();
    state.MarkProbeResult(success: false, at: DateTimeOffset.UnixEpoch, failedTransport: "PostgresSignalTransport");

    var detail = state.Report().Detail!;

    await Assert.That(detail).Contains("PostgresSignalTransport");
    await Assert.That(detail).DoesNotContain("running on polling fallback")
      .Because("the verdict does not change how pumps run; it only feeds this component");
    await Assert.That(detail).Contains("if the route is down");
  }
}
