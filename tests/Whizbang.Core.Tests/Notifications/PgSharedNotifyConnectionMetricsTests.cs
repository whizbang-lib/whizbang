using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Regression locks for the NOTIFY-side observability surface added on
/// release/v0.493.0-alpha.1:
/// <list type="bullet">
///   <item><description><c>NotifyMetrics.SignalsReceived</c> increments per delivered
///   notification, tagged with <c>category</c> (outbox/inbox/perspective/unknown).</description></item>
///   <item><description><c>NotifyMetrics.ConnectionState</c> records +1 when the gate becomes
///   available, -1 when it goes back down.</description></item>
///   <item><description><c>NotifyMetrics.SignalingMode</c> counts every state transition, tagged
///   with <c>mode</c> and <c>reason</c>, paired with a structured Information log.</description></item>
/// </list>
/// <para>All three are passive counters (#711): they accumulate in memory and report one CUMULATIVE
/// reading per series only when a listener collects, so the recorder below collects on every read
/// and the assertions pick out the series they care about by tag and value.</para>
/// </summary>
/// <docs>operations/observability/metrics</docs>
public class PgSharedNotifyConnectionMetricsTests {

  private sealed class NoOpSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public void OnNotification(string payload) { }
  }

  private sealed record Measurement(string Name, long Value, IReadOnlyDictionary<string, object?> Tags);

  private sealed class MeasurementRecorder : IDisposable {
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<Measurement> _measurements = [];
    private readonly HashSet<Instrument> _interestedInstruments;

    public MeasurementRecorder(NotifyMetrics metrics) {
      // Tests run in parallel; multiple NotifyMetrics instances publish on the same
      // meter name. Filter by EXACT instrument identity so this recorder only sees
      // measurements from the metrics instance under test.
      _interestedInstruments = [
        metrics.SignalsReceived.Instrument,
        metrics.ConnectionState.Instrument,
        metrics.SignalingMode.Instrument,
      ];
      _listener = new MeterListener {
        InstrumentPublished = (instrument, l) => {
          if (_interestedInstruments.Contains(instrument)) {
            l.EnableMeasurementEvents(instrument);
          }
        },
      };
      _listener.SetMeasurementEventCallback<long>(_recordLong);
      _listener.SetMeasurementEventCallback<int>(_recordInt);
      _listener.Start();
    }

    /// <summary>
    /// Collects the passive counters (#711) and returns one cumulative reading per series: the
    /// untagged series of each instrument, every closed-domain series the constructor seeded
    /// (category=outbox/inbox/perspective/unknown, mode=listen_notify/polling_only; zero until used),
    /// and one per tag set added since construction. Earlier readings are discarded so each read is
    /// a snapshot of the current state, not an accumulation of collections.
    /// </summary>
    public IReadOnlyCollection<Measurement> Measurements {
      get {
        _measurements.Clear();
        _listener.RecordObservableInstruments();
        return _measurements.ToArray();
      }
    }

    private void _recordLong(Instrument inst, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _) {
      _measurements.Add(new Measurement(inst.Name, value, _materialize(tags)));
    }

    private void _recordInt(Instrument inst, int value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _) {
      _measurements.Add(new Measurement(inst.Name, value, _materialize(tags)));
    }

    private static Dictionary<string, object?> _materialize(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      var dict = new Dictionary<string, object?>(tags.Length);
      foreach (var kvp in tags) {
        dict[kvp.Key] = kvp.Value;
      }
      return dict;
    }

    public void Dispose() => _listener.Dispose();
  }

  private static (PgSharedNotifyConnection conn, NotifyMetrics metrics) _build() {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var metrics = new NotifyMetrics(new WhizbangMetrics());
    var conn = new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling }),
      cfg,
      new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null,
      notificationDataSource: null,
      metrics: metrics);
    return (conn, metrics);
  }

  private static void _invokeDispatch(PgSharedNotifyConnection conn, string channel, string payload) {
    var argsType = typeof(global::Npgsql.NpgsqlNotificationEventArgs);
    var evArgs = (global::Npgsql.NpgsqlNotificationEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(argsType);
    var channelField = argsType.GetField("<Channel>k__BackingField",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var payloadField = argsType.GetField("<Payload>k__BackingField",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    channelField.SetValue(evArgs, channel);
    payloadField.SetValue(evArgs, payload);
    var method = typeof(PgSharedNotifyConnection).GetMethod(
      "_dispatchNotification",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    method.Invoke(conn, [null, evArgs]);
  }

  private static void _invokeSetAvailable(PgSharedNotifyConnection conn, bool available, string? failureReason) {
    var method = typeof(PgSharedNotifyConnection).GetMethod(
      "_setAvailable",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    method.Invoke(conn, [available, failureReason]);
  }

  /// <summary>
  /// The series of one instrument that actually counted something. A passive counter (#711) also
  /// reports its untagged series and the constructor's closed-domain seeds, all at zero.
  /// </summary>
  private static List<Measurement> _counted(IReadOnlyCollection<Measurement> readings, string name) =>
    readings.Where(m => m.Name == name && m.Value != 0).ToList();

  /// <summary>The cumulative connection-state reading: an untagged up-down counter with a single series.</summary>
  private static long _connectionState(IReadOnlyCollection<Measurement> readings) =>
    readings.Single(m => m.Name == "whizbang.postgres.notifications.connection_state").Value;

  [Test]
  public async Task Constructor_SeedsEveryCounterAtZero_PerClosedTagValueAsync() {
    // Issue #711: a pushed counter exports no series until its first measurement; a passive one
    // holds every closed-domain series at zero from construction and reports them all at every
    // collection. Build on a per-test meter factory and filter by meter INSTANCE so parallel
    // instances on the same meter name can neither add series to this reading nor remove ours.
    using var factory = new TestMeterFactory();
    var zeros = new List<(string Name, string? Tag)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (factory.CreatedMeters.Contains(instrument.Meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((i, v, tags, _) => _addZero(zeros, i, v, tags));
    listener.SetMeasurementEventCallback<int>((i, v, tags, _) => _addZero(zeros, i, v, tags));
    listener.Start();

    _ = new NotifyMetrics(new WhizbangMetrics(factory));
    listener.RecordObservableInstruments();

    List<(string Name, string? Tag)> snapshot;
    lock (zeros) {
      snapshot = [.. zeros];
    }
    var categories = snapshot.Where(z => z.Name == "whizbang.postgres.notifications.signals_received").Select(z => z.Tag).ToList();
    foreach (var category in new[] { "outbox", "inbox", "perspective", "unknown" }) {
      await Assert.That(categories).Contains($"category={category}")
        .Because("each doorbell category is a closed domain and gets its own zero series");
    }
    await Assert.That(snapshot.Any(z => z.Name == "whizbang.postgres.notifications.connection_state")).IsTrue();
    var modes = snapshot.Where(z => z.Name == "whizbang.postgres.notifications.signaling_mode").Select(z => z.Tag).ToList();
    await Assert.That(modes).Contains("mode=listen_notify");
    await Assert.That(modes).Contains("mode=polling_only");
  }

  private static void _addZero<T>(List<(string Name, string? Tag)> zeros, Instrument instrument, T value,
      ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct {
    if (!value.Equals(default(T))) {
      return;
    }
    string? first = null;
    foreach (var tag in tags) {
      first = $"{tag.Key}={tag.Value}";
      break;
    }
    lock (zeros) {
      zeros.Add((instrument.Name, first));
    }
  }

  [Test]
  public async Task SignalsReceived_OutboxPayload_TaggedCategoryOutboxAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "outbox");

    // Cumulative per series (#711): every category series is reported, only outbox counted.
    var signal = _counted(recorder.Measurements, "whizbang.postgres.notifications.signals_received");
    await Assert.That(signal).Count().IsEqualTo(1);
    await Assert.That(signal[0].Value).IsEqualTo(1L);
    await Assert.That(signal[0].Tags["category"]).IsEqualTo("outbox");
  }

  [Test]
  public async Task SignalsReceived_InboxPayload_TaggedCategoryInboxAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "inbox");

    var signal = _counted(recorder.Measurements, "whizbang.postgres.notifications.signals_received").Single();
    await Assert.That(signal.Value).IsEqualTo(1L);
    await Assert.That(signal.Tags["category"]).IsEqualTo("inbox");
  }

  [Test]
  public async Task SignalsReceived_PerspectivePayload_TaggedCategoryPerspectiveAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "perspective");

    var signal = _counted(recorder.Measurements, "whizbang.postgres.notifications.signals_received").Single();
    await Assert.That(signal.Value).IsEqualTo(1L);
    await Assert.That(signal.Tags["category"]).IsEqualTo("perspective");
  }

  [Test]
  public async Task SignalsReceived_UnknownPayload_TaggedCategoryUnknownAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "something-new-from-sql");

    var signal = _counted(recorder.Measurements, "whizbang.postgres.notifications.signals_received").Single();
    await Assert.That(signal.Value).IsEqualTo(1L);
    await Assert.That(signal.Tags["category"]).IsEqualTo("unknown");
  }

  [Test]
  public async Task SignalsReceived_NoSubscribers_DoesNotIncrementAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);

    _invokeDispatch(conn, "no-subscribers-here", "outbox");

    // The category series all exist from construction (#711); none of them may have counted.
    var signals = _counted(recorder.Measurements, "whizbang.postgres.notifications.signals_received");
    await Assert.That(signals).IsEmpty()
      .Because("nothing was delivered on a channel with no subscriber, so every category series stays at its zero seed");
  }

  [Test]
  public async Task ConnectionState_TransitionToAvailable_Records_Plus_OneAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);

    _invokeSetAvailable(conn, true, null);

    var state = recorder.Measurements
      .Where(m => m.Name == "whizbang.postgres.notifications.connection_state")
      .ToList();
    await Assert.That(state).Count().IsEqualTo(1);
    await Assert.That(state[0].Value).IsEqualTo(1L);
  }

  [Test]
  public async Task ConnectionState_TransitionToUnavailable_Records_Minus_OneAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    // First go available so the next transition fires. The up-down counter is cumulative (#711):
    // a +1 followed by a -1 reads back as zero, so read it while up to see the step down.
    _invokeSetAvailable(conn, true, null);
    var whileUp = _connectionState(recorder.Measurements);

    _invokeSetAvailable(conn, false, "test-reason");

    var afterDrop = _connectionState(recorder.Measurements);
    await Assert.That(whileUp).IsEqualTo(1L);
    await Assert.That(afterDrop - whileUp).IsEqualTo(-1L);
    await Assert.That(afterDrop).IsEqualTo(0L)
      .Because("a dropped connection steps the up-down counter back so the sum across pods counts only live LISTEN/NOTIFY connections");
  }

  [Test]
  public async Task SignalingMode_AvailableTransition_TaggedListenNotifyAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);

    _invokeSetAvailable(conn, true, null);

    // Both mode seeds are reported at zero (#711); the transition is the one series that counted.
    var mode = _counted(recorder.Measurements, "whizbang.postgres.notifications.signaling_mode").Single();
    await Assert.That(mode.Value).IsEqualTo(1L);
    await Assert.That(mode.Tags["mode"]).IsEqualTo("listen_notify");
  }

  [Test]
  public async Task SignalingMode_UnavailableTransition_TaggedPollingOnlyAsync() {
    var (conn, metrics) = _build();
    _invokeSetAvailable(conn, true, null);
    using var recorder = new MeasurementRecorder(metrics);

    _invokeSetAvailable(conn, false, "probe failed");

    // Cumulative (#711): the earlier restore still reads 1 under listen_notify; the fallback
    // under test is the polling_only series, tagged with the failure reason.
    var counted = _counted(recorder.Measurements, "whizbang.postgres.notifications.signaling_mode");
    var modeMeasurement = counted.Single(m => m.Tags["mode"] is "polling_only");
    await Assert.That(modeMeasurement.Value).IsEqualTo(1L);
    await Assert.That(modeMeasurement.Tags["mode"]).IsEqualTo("polling_only");
    await Assert.That(modeMeasurement.Tags["reason"]).IsEqualTo("probe failed");
  }

  [Test]
  public async Task ConnectionState_NoStateChange_NotRecordedAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    _invokeSetAvailable(conn, true, null);
    var afterFirst = _connectionState(recorder.Measurements);

    // Second call with the same state should not fire — _setAvailable guards transitions.
    _invokeSetAvailable(conn, true, null);

    await Assert.That(afterFirst).IsEqualTo(1L);
    await Assert.That(_connectionState(recorder.Measurements)).IsEqualTo(afterFirst)
      .Because("a repeated same-state call is not a transition; the cumulative state must not climb past one live connection");
  }
}
