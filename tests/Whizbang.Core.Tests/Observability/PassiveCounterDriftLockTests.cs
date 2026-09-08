using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Every counter and up-down counter the framework owns is passive (issue #711): the process
/// accumulates the count and the meter reports it at collection, so the series exists from
/// construction, at zero. A pushed counter exports nothing until its first measurement; after a
/// deploy with no dead-letter activity every dead-letter series vanished from the dashboards and
/// the meter looked stopped, with the registration and the subscription both correct. A subsystem
/// that is quiet because it is healthy must read as zero, not as absent. Histograms keep the push
/// model (a distribution has no value before its first sample) and gauges never had the gap.
/// </summary>
/// <remarks>
/// The reflection sweep is the drift-lock: a pushed counter on any Core metrics class, or a passive
/// one that does not report at collection, fails here without a per-class test having to know about
/// it. The three counter owners that are not metrics classes (the discard policy, the poison
/// detector, the broker dead-letter drain worker) get explicit cases below.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/PassiveCounter.cs</code-under-test>
/// <docs>operations/observability/metrics</docs>
[Category("Shard2")]
public class PassiveCounterDriftLockTests {

  [Test]
  public async Task EveryCoreMetricsClass_ReportsEveryCounterAtTheFirstCollectionAsync() {
    var metricsClasses = typeof(WhizbangMetrics).Assembly.GetTypes()
      .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == typeof(WhizbangMetrics).Namespace
                  && t.Name.EndsWith("Metrics", StringComparison.Ordinal) && t != typeof(WhizbangMetrics))
      .Where(t => t.GetConstructors().Any(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(WhizbangMetrics)))
      .OrderBy(t => t.Name, StringComparer.Ordinal)
      .ToList();

    await Assert.That(metricsClasses.Select(t => t.Name)).Contains(nameof(DeadLetterMetrics))
      .Because("the sweep must actually find the metrics classes, or it locks nothing");

    var unseeded = new List<string>();
    foreach (var metricsClass in metricsClasses) {
      using var factory = new TestMeterFactory();
      var recorder = new CounterRecorder(factory);
      recorder.Start();
      _ = _construct(metricsClass, factory);
      foreach (var missing in recorder.CountersWithoutAMeasurement()) {
        unseeded.Add($"{metricsClass.Name}: {missing}");
      }
      recorder.Dispose();
    }

    await Assert.That(unseeded).IsEmpty()
      .Because("a counter that reports nothing at collection exports no series, so a healthy, quiet subsystem reads as a stopped meter: "
             + string.Join(", ", unseeded));
  }

  [Test]
  public async Task DeadLetterMetrics_EveryCounter_ReadsZeroBeforeAnyDeadLetterAsync() {
    using var factory = new TestMeterFactory();
    var recorder = new CounterRecorder(factory);
    recorder.Start();

    _ = new DeadLetterMetrics(new WhizbangMetrics(factory));
    recorder.Collect();

    await Assert.That(recorder.CountersWithoutAMeasurement()).IsEmpty();
    await Assert.That(recorder.Measurements.All(m => m.Value == 0)).IsTrue()
      .Because("the seed is a zero, never a fabricated count");
    await Assert.That(recorder.Measurements.Select(m => m.Instrument)).Contains("whizbang.dead_letters.added");
    recorder.Dispose();
  }

  [Test]
  public async Task DeadLetterMetrics_ClosedTagDomains_ReportAtZeroPerValueAsync() {
    using var factory = new TestMeterFactory();
    var recorder = new CounterRecorder(factory);
    recorder.Start();

    _ = new DeadLetterMetrics(new WhizbangMetrics(factory));
    recorder.Collect();

    var addedTables = recorder.Measurements
      .Where(m => m.Instrument == "whizbang.dead_letters.added")
      .Select(m => m.Tags.GetValueOrDefault("source_table"))
      .ToList();
    await Assert.That(addedTables).Contains(DeadLetterSourceTable.INBOX);
    await Assert.That(addedTables).Contains(DeadLetterSourceTable.OUTBOX);
    await Assert.That(addedTables).Contains(DeadLetterSourceTable.PERSPECTIVE_EVENTS);
    var verdicts = recorder.Measurements
      .Where(m => m.Instrument == "whizbang.dead_letters.cohort_verdicts")
      .Select(m => m.Tags.GetValueOrDefault("verdict"))
      .ToList();
    await Assert.That(verdicts).Contains(nameof(CanaryVerdictKind.Pass))
      .Because("a closed tag domain seeds one zero series per known value, so a per-verdict panel exists before the first verdict");
    recorder.Dispose();
  }

  [Test]
  public async Task MessageDiscardPolicy_SkippedCounter_ReadsZeroPerGateAtConstructionAsync() {
    var meter = new Meter("Whizbang.Tests.CounterSeeding.Discard");
    var recorder = new CounterRecorder(meterName: meter.Name);
    recorder.Start();

    _ = new MessageDiscardPolicy(new NoReceptors(), NullLogger<MessageDiscardPolicy>.Instance, meter);
    recorder.Collect();

    var gates = recorder.Measurements
      .Where(m => m.Instrument == MessageDiscardPolicy.COUNTER_NAME)
      .Select(m => m.Tags.GetValueOrDefault("gate"))
      .ToList();
    await Assert.That(gates).Contains("receive");
    await Assert.That(gates).Contains("inbox");
    await Assert.That(gates).Contains("outbox");
    recorder.Dispose();
  }

  [Test]
  public async Task PoisonMessageDetector_QuarantinedCounter_ReadsZeroPerGateAtConstructionAsync() {
    var meter = new Meter("Whizbang.Tests.CounterSeeding.Poison");
    var recorder = new CounterRecorder(meterName: meter.Name);
    recorder.Start();

    _ = new PoisonMessageDetector(
      Options.Create(new PoisonMessageOptions()), NullLogger<PoisonMessageDetector>.Instance, meter);
    recorder.Collect();

    var gates = recorder.Measurements
      .Where(m => m.Instrument == PoisonMessageDetector.COUNTER_NAME)
      .Select(m => m.Tags.GetValueOrDefault("gate"))
      .ToList();
    await Assert.That(gates).Contains("receive");
    await Assert.That(gates).Contains("inbox");
    recorder.Dispose();
  }

  [Test]
  public async Task TransportDeadLetterDrainWorker_DrainedCounter_ReadsZeroAtConstructionAsync() {
    using var factory = new TestMeterFactory();
    var recorder = new CounterRecorder(factory);
    recorder.Start();
    var provider = new ServiceCollection().BuildServiceProvider();

    _ = new TransportDeadLetterDrainWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new TransportDeadLetterDrainWorkerOptions()),
      whizbangMetrics: new WhizbangMetrics(factory),
      logger: NullLogger<TransportDeadLetterDrainWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady());
    recorder.Collect();

    await Assert.That(recorder.Measurements.Any(m => m.Instrument == "whizbang.transport_dlq.drained" && m.Value == 0)).IsTrue()
      .Because("a broker dead-letter queue that has drained nothing since the restart is healthy, not unmonitored");
    recorder.Dispose();
  }

  // -------------------------------------------------------------------------------------------

  private static object _construct(Type metricsClass, TestMeterFactory factory) {
    var ctor = metricsClass.GetConstructors()
      .First(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(WhizbangMetrics));
    var args = ctor.GetParameters()
      .Select(p => p.ParameterType == typeof(WhizbangMetrics) ? new WhizbangMetrics(factory)
                 : p.HasDefaultValue ? p.DefaultValue
                 : null)
      .ToArray();
    return ctor.Invoke(args);
  }

  private sealed class NoReceptors : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
  }

  /// <summary>
  /// Records every measurement on every counter and up-down counter of the meters it watches
  /// (all meters when constructed without a name), and knows which of those instruments never
  /// measured anything. Started BEFORE the metrics object is constructed so the constructor's
  /// seeds are observed.
  /// </summary>
  private sealed class CounterRecorder(TestMeterFactory? factory = null, string? meterName = null) : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<Instrument> _counters = [];
    private readonly HashSet<Instrument> _measured = [];
    private volatile bool _accepting;
    public List<(string Instrument, long Value, Dictionary<string, string?> Tags)> Measurements { get; } = [];

    public void Start() {
      _listener.InstrumentPublished = (instrument, l) => {
        // Start() replays every instrument already alive in the process; only instruments
        // published after that are ours. With a per-test meter factory the meter identity is the
        // filter (parallel tests constructing the same class cannot interleave); a meter name is
        // the filter for owners that create their own Meter.
        if (!_accepting) {
          return;
        }
        var ours = factory is not null ? factory.CreatedMeters.Contains(instrument.Meter) : instrument.Meter.Name == meterName;
        if (!ours) {
          return;
        }
        if (!_isCounter(instrument)) {
          return;
        }
        lock (_counters) {
          _counters.Add(instrument);
        }
        l.EnableMeasurementEvents(instrument);
      };
      _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => _record(i, v, tags));
      _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => _record(i, v, tags));
      _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => _record(i, (long)v, tags));
      _listener.Start();
      _accepting = true;
    }

    /// <summary>What an exporter does: ask every observable instrument for its current values.</summary>
    public void Collect() => _listener.RecordObservableInstruments();

    public List<string> CountersWithoutAMeasurement() {
      Collect();
      lock (_counters) {
        return _counters.Where(c => !_measured.Contains(c)).Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList();
      }
    }

    private static bool _isCounter(Instrument instrument) {
      var type = instrument.GetType();
      if (!type.IsGenericType) {
        return false;
      }
      var definition = type.GetGenericTypeDefinition();
      return definition == typeof(Counter<>) || definition == typeof(UpDownCounter<>)
        || definition == typeof(ObservableCounter<>) || definition == typeof(ObservableUpDownCounter<>);
    }

    private void _record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
      foreach (var tag in tags) {
        dict[tag.Key] = tag.Value?.ToString();
      }
      lock (_counters) {
        _measured.Add(instrument);
        Measurements.Add((instrument.Name, value, dict));
      }
    }

    public void Dispose() => _listener.Dispose();
  }
}
