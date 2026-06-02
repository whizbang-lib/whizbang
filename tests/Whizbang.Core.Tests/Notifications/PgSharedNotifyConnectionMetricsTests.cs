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
///   <item><description><c>NotifyMetrics.SignalingMode</c> emits a measurement tagged with
///   <c>mode</c> on every state transition, paired with a structured Information log.</description></item>
/// </list>
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
        metrics.SignalsReceived,
        metrics.ConnectionState,
        metrics.SignalingMode,
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

    public IReadOnlyCollection<Measurement> Measurements => _measurements;

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

  [Test]
  public async Task SignalsReceived_OutboxPayload_TaggedCategoryOutboxAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "outbox");

    var signal = recorder.Measurements
      .Where(m => m.Name == "whizbang.postgres.notifications.signals_received")
      .ToList();
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

    var tag = recorder.Measurements
      .Single(m => m.Name == "whizbang.postgres.notifications.signals_received")
      .Tags["category"];
    await Assert.That(tag).IsEqualTo("inbox");
  }

  [Test]
  public async Task SignalsReceived_PerspectivePayload_TaggedCategoryPerspectiveAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "perspective");

    var tag = recorder.Measurements
      .Single(m => m.Name == "whizbang.postgres.notifications.signals_received")
      .Tags["category"];
    await Assert.That(tag).IsEqualTo("perspective");
  }

  [Test]
  public async Task SignalsReceived_UnknownPayload_TaggedCategoryUnknownAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);
    using var handle = conn.Subscribe(new NoOpSubscription("ch"));

    _invokeDispatch(conn, "ch", "something-new-from-sql");

    var tag = recorder.Measurements
      .Single(m => m.Name == "whizbang.postgres.notifications.signals_received")
      .Tags["category"];
    await Assert.That(tag).IsEqualTo("unknown");
  }

  [Test]
  public async Task SignalsReceived_NoSubscribers_DoesNotIncrementAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);

    _invokeDispatch(conn, "no-subscribers-here", "outbox");

    var signals = recorder.Measurements
      .Where(m => m.Name == "whizbang.postgres.notifications.signals_received")
      .ToList();
    await Assert.That(signals).IsEmpty();
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
    // First go available so the next transition fires.
    _invokeSetAvailable(conn, true, null);
    using var recorder = new MeasurementRecorder(metrics);

    _invokeSetAvailable(conn, false, "test-reason");

    var state = recorder.Measurements
      .Where(m => m.Name == "whizbang.postgres.notifications.connection_state")
      .ToList();
    await Assert.That(state).Count().IsEqualTo(1);
    await Assert.That(state[0].Value).IsEqualTo(-1L);
  }

  [Test]
  public async Task SignalingMode_AvailableTransition_TaggedListenNotifyAsync() {
    var (conn, metrics) = _build();
    using var recorder = new MeasurementRecorder(metrics);

    _invokeSetAvailable(conn, true, null);

    var mode = recorder.Measurements
      .Single(m => m.Name == "whizbang.postgres.notifications.signaling_mode")
      .Tags["mode"];
    await Assert.That(mode).IsEqualTo("listen_notify");
  }

  [Test]
  public async Task SignalingMode_UnavailableTransition_TaggedPollingOnlyAsync() {
    var (conn, metrics) = _build();
    _invokeSetAvailable(conn, true, null);
    using var recorder = new MeasurementRecorder(metrics);

    _invokeSetAvailable(conn, false, "probe failed");

    var modeMeasurement = recorder.Measurements
      .Single(m => m.Name == "whizbang.postgres.notifications.signaling_mode");
    await Assert.That(modeMeasurement.Tags["mode"]).IsEqualTo("polling_only");
    await Assert.That(modeMeasurement.Tags["reason"]).IsEqualTo("probe failed");
  }

  [Test]
  public async Task ConnectionState_NoStateChange_NotRecordedAsync() {
    var (conn, metrics) = _build();
    _invokeSetAvailable(conn, true, null);
    using var recorder = new MeasurementRecorder(metrics);

    // Second call with the same state should not fire — _setAvailable guards transitions.
    _invokeSetAvailable(conn, true, null);

    var state = recorder.Measurements
      .Where(m => m.Name == "whizbang.postgres.notifications.connection_state")
      .ToList();
    await Assert.That(state).IsEmpty();
  }
}
