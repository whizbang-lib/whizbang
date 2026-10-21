// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The sampler that reads the event store by event type: once per window, on the one instance that takes the
/// window, with every other instance reporting nothing.
/// </summary>
/// <remarks>
/// Every wait is on a signal: the sample's completion, or the sampler arming its timer for the next window on a
/// fake clock, which the test then advances. Nothing sleeps.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/EventTypeBytesSampler.cs</code-under-test>
/// <docs>operations/observability/metrics#event-type-bytes</docs>
[Category("Core")]
[Category("Observability")]
public class EventTypeBytesSamplerTests {
  private const string ORDER_PLACED = "Shop.OrderPlaced, Shop";
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

  // 00:07 into a fifteen-minute window that started at midnight.
  private static readonly DateTimeOffset _start = new(2026, 1, 1, 0, 7, 0, TimeSpan.Zero);

  private static readonly Dictionary<string, EventTypeBytes> _sample = new() {
    [ORDER_PLACED] = new(SampledRows: 12, MeanDataBytes: 300, MeanMetadataBytes: 600, EstimatedTotalBytes: 1_080_000),
  };

  [Test]
  public async Task TheInstanceThatTakesTheWindow_ReportsItsSample_UnderTheWindowsClaimAsync() {
    var provider = new ScriptedProvider(_sample);
    await using var host = new SamplerHost(provider, new TableStatisticsOptions {
      EventTypeSamplePercent = 2.5,
      MaxEventTypes = 7,
    });

    var took = await host.StartAndWaitForSampleAsync();
    var readings = host.Read();

    var window = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    await Assert.That(took).IsTrue();
    await Assert.That(provider.Requests[0]).IsEqualTo(new EventTypeSampleRequest(
      "whizbang:event-type-bytes:" + window.ToString(CultureInfo.InvariantCulture), 2.5, 7))
      .Because("every instance names the window the same way, from the clock, so exactly one of them gets it");
    await Assert.That(readings).Contains(("whizbang.event_store.estimated_type_bytes", 1_080_000d, ORDER_PLACED));
  }

  [Test]
  public async Task AnInstanceThatDoesNotTakeTheNextWindow_ClearsItsReadingAsync() {
    // An instance that reported the last window and then lost the claim must not keep reporting it: two
    // instances would then report the same store, and a dashboard summing them would double it.
    var provider = new ScriptedProvider(_sample, null);
    await using var host = new SamplerHost(provider, new TableStatisticsOptions());

    await host.StartAndWaitForSampleAsync();
    var before = host.Read();
    await host.Clock.WaitForArmedAsync(TimeSpan.FromMinutes(8), _wait);
    var next = host.NextSample();
    host.Clock.Advance(TimeSpan.FromMinutes(8));
    var took = await next.WaitAsync(_wait);

    await Assert.That(before).IsNotEmpty();
    await Assert.That(took).IsFalse();
    await Assert.That(host.Read()).IsEmpty();
    var nextWindow = new DateTimeOffset(2026, 1, 1, 0, 15, 0, TimeSpan.Zero).UtcTicks;
    await Assert.That(provider.Requests[1].ClaimKey).IsEqualTo("whizbang:event-type-bytes:" + nextWindow.ToString(CultureInfo.InvariantCulture))
      .Because("the sampler waits for the start of the next window, the moment every instance asks for it");
  }

  [Test]
  public async Task AFailedSample_IsLogged_AndTheNextWindowIsStillSampledAsync() {
    var provider = new ScriptedProvider(new InvalidOperationException("the store refused"), _sample);
    await using var host = new SamplerHost(provider, new TableStatisticsOptions());

    var next = host.NextSample();
    await host.StartAsync();
    await host.Clock.WaitForArmedAsync(TimeSpan.FromMinutes(8), _wait);
    host.Clock.Advance(TimeSpan.FromMinutes(8));
    var took = await next.WaitAsync(_wait);

    await Assert.That(took).IsTrue()
      .Because("one failed read costs one window, never the sampler");
    await Assert.That(provider.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AZeroInterval_NeverAsksTheProviderAsync() {
    var provider = new ScriptedProvider(_sample);
    await using var host = new SamplerHost(provider, new TableStatisticsOptions { EventTypeSampleInterval = TimeSpan.Zero });

    await host.StartAsync();
    await host.Sampler.ExecuteTask!.WaitAsync(_wait);

    await Assert.That(provider.Requests).IsEmpty()
      .Because("zero is the documented way to turn the sample off");
  }

  [Test]
  public async Task WithNoProvider_TheSamplerStopsAsync() {
    await using var host = new SamplerHost(provider: null, new TableStatisticsOptions());

    await host.StartAsync();
    await host.Sampler.ExecuteTask!.WaitAsync(_wait);

    await Assert.That(host.Sampler.ExecuteTask.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task AProviderDisposedUnderIt_StopsTheSamplerAsync() {
    // The host disposes its services while shutting down, and a read that lands then is not a failure to log.
    var provider = new ScriptedProvider(new ObjectDisposedException("data source"));
    await using var host = new SamplerHost(provider, new TableStatisticsOptions());

    await host.StartAsync();
    await host.Sampler.ExecuteTask!.WaitAsync(_wait);

    await Assert.That(provider.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task BeforeTheSchemaIsReady_NothingIsSampled_AndStoppingEndsTheWaitAsync() {
    // Stopped only once the sampler is waiting on the gate: stopped any earlier, the host cancels the task before
    // it starts, which proves nothing about the wait and ends the task canceled rather than returned.
    var provider = new ScriptedProvider(_sample);
    var gate = new NeverReadyGate();
    await using var host = new SamplerHost(provider, new TableStatisticsOptions(), gate);

    await host.StartAsync();
    await gate.Waiting.WaitAsync(_wait);
    await host.StopAsync();

    await Assert.That(host.Sampler.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(provider.Requests).IsEmpty()
      .Because("the event store does not exist until the schema is ready");
  }

  [Test]
  public async Task WithoutALoggerOrAClock_TheSamplerStillRunsAsync() {
    var provider = new ScriptedProvider(_sample);
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(provider);
    await using var container = services.BuildServiceProvider();
    using var factory = new TestMeterFactory();
    var sampler = new EventTypeBytesSampler(
      container.GetRequiredService<IServiceScopeFactory>(),
      new TableStatisticsMetrics(new WhizbangMetrics(factory)),
      Options.Create(new TableStatisticsOptions()),
      schemaReadyGate: null,
      logger: null);
    var sampled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    sampler.SampleCompleted += took => sampled.TrySetResult(took);

    using var cts = new CancellationTokenSource();
    await sampler.StartAsync(cts.Token);
    var took = await sampled.Task.WaitAsync(_wait);
    await cts.CancelAsync();
    await sampler.StopAsync(CancellationToken.None);

    await Assert.That(took).IsTrue();
  }

  [Test]
  public async Task WithoutOptions_TheSamplerRefusesToBeBuiltAsync() {
    await using var container = new ServiceCollection().BuildServiceProvider();
    using var factory = new TestMeterFactory();

    await Assert.That(() => new EventTypeBytesSampler(
        container.GetRequiredService<IServiceScopeFactory>(),
        new TableStatisticsMetrics(new WhizbangMetrics(factory)),
        options: null!,
        schemaReadyGate: null,
        logger: null))
      .Throws<ArgumentNullException>()
      .Because("the interval, share and cap all come from the options; there is no safe default for a missing one");
  }

  /// <summary>A sampler on a fake clock, with a listener on its own meter.</summary>
  private sealed class SamplerHost : IAsyncDisposable {
    private readonly ServiceProvider _container;
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, double Value, string? EventType)> _readings = [];
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource<bool> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SamplerHost(ScriptedProvider? provider, TableStatisticsOptions options, ISchemaReadyGate? gate = null) {
      var services = new ServiceCollection();
      if (provider is not null) {
        services.AddSingleton<ITableStatisticsProvider>(provider);
      }
      _container = services.BuildServiceProvider();
      var metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      Sampler = new EventTypeBytesSampler(
        _container.GetRequiredService<IServiceScopeFactory>(),
        metrics,
        Options.Create(options),
        gate ?? SchemaReadyGate.AlreadyReady(),
        NullLogger<EventTypeBytesSampler>.Instance,
        Clock);
      Sampler.SampleCompleted += took => Volatile.Read(ref _next).TrySetResult(took);

      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter && instrument.Name.StartsWith("whizbang.event_store.", StringComparison.Ordinal)) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.Start();
    }

    public ArmedTimerClock Clock { get; } = new(_start);

    public EventTypeBytesSampler Sampler { get; }

    /// <summary>The next sample's completion, with whether this instance took the window.</summary>
    public Task<bool> NextSample() {
      var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      Volatile.Write(ref _next, next);
      return next.Task;
    }

    public Task StartAsync() => Sampler.StartAsync(_stopping.Token);

    public async Task<bool> StartAndWaitForSampleAsync() {
      var next = NextSample();
      await StartAsync();
      return await next.WaitAsync(_wait);
    }

    public async Task StopAsync() {
      await _stopping.CancelAsync();
      await Sampler.StopAsync(CancellationToken.None);
    }

    public List<(string Name, double Value, string? EventType)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? eventType = null;
      foreach (var tag in tags) {
        if (tag.Key == "event_type") {
          eventType = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, value, eventType));
    }

    public async ValueTask DisposeAsync() {
      await StopAsync();
      _listener.Dispose();
      _factory.Dispose();
      _stopping.Dispose();
      await _container.DisposeAsync();
    }
  }

  /// <summary>Answers each sample with the next scripted outcome: a sample, null (not this instance's window) or a failure.</summary>
  private sealed class ScriptedProvider(params object?[] outcomes) : ITableStatisticsProvider {
    private int _calls;

    public List<EventTypeSampleRequest> Requests { get; } = [];

    public Task<IReadOnlyDictionary<string, EventTypeBytes>?> SampleEventTypeBytesAsync(
        EventTypeSampleRequest request, CancellationToken ct = default) {
      lock (Requests) {
        Requests.Add(request);
      }
      var outcome = outcomes[Math.Min(Interlocked.Increment(ref _calls) - 1, outcomes.Length - 1)];
      return outcome is Exception failure
        ? Task.FromException<IReadOnlyDictionary<string, EventTypeBytes>?>(failure)
        : Task.FromResult((IReadOnlyDictionary<string, EventTypeBytes>?)outcome);
    }

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
  }

  /// <summary>A schema gate that never opens, and reports when something starts waiting on it.</summary>
  private sealed class NeverReadyGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Waiting => _waiting.Task;

    public bool IsReady => false;

    public void MarkReady() => throw new NotSupportedException("this gate never opens");

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waiting.TrySetResult();
      return Task.Delay(Timeout.Infinite, cancellationToken);
    }
  }

  /// <summary>A fake clock that reports each timer the sampler arms, with its due time.</summary>
  private sealed class ArmedTimerClock(DateTimeOffset start) : FakeTimeProvider(start) {
    private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      _armed.Writer.TryWrite(dueTime);
      return timer;
    }

    /// <summary>Completes once a timer with <paramref name="due"/> has been armed.</summary>
    public async Task WaitForArmedAsync(TimeSpan due, TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      TimeSpan armed;
      do {
        armed = await _armed.Reader.ReadAsync(cts.Token);
      } while (armed != due);
    }
  }
}
