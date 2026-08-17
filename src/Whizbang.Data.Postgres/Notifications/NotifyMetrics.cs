using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Metrics for the Postgres LISTEN/NOTIFY work-signaling path.
/// </summary>
/// <remarks>
/// <para>
/// Meter name: <c>Whizbang.Postgres.Notifications</c>.
/// </para>
/// <para>
/// Surfaces three observables:
/// </para>
/// <list type="bullet">
///   <item><description><b>SignalsReceived</b> — per-NOTIFY counter tagged by <c>category</c>
///   (<c>outbox</c>, <c>inbox</c>, <c>perspective</c>, <c>unknown</c>). Drives "are we actually
///   getting NOTIFY traffic and what kind" dashboards.</description></item>
///   <item><description><b>ConnectionState</b> — UpDownCounter that records the current
///   LISTEN/NOTIFY connection availability (1 when healthy and probe-verified, 0 when
///   disconnected / falling back to polling). Sum across pods tells you how many connections
///   are actually live at any moment.</description></item>
///   <item><description><b>SignalingMode</b> — counter incremented once per pod startup AND on
///   every runtime mode transition, tagged by <c>mode</c> (<c>listen_notify</c>,
///   <c>polling_only</c>) and <c>reason</c>. Pair with the structured "Whizbang signaling
///   mode: ..." Information log for a complete audit of why a pod is in a given mode.</description></item>
/// </list>
/// </remarks>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgSharedNotifyConnectionMetricsTests.cs:SignalsReceived_OutboxPayload_TaggedCategoryOutboxAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgSharedNotifyConnectionMetricsTests.cs:ConnectionState_TransitionToAvailable_Records_Plus_OneAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgSharedNotifyConnectionMetricsTests.cs:SignalingMode_AvailableTransition_TaggedListenNotifyAsync</tests>
public sealed class NotifyMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Postgres.Notifications";
#pragma warning restore CA1707

  /// <summary>NOTIFY signals delivered to subscribers, tagged by <c>category</c>.</summary>
  public Counter<long> SignalsReceived { get; }

  /// <summary>+1 when LISTEN/NOTIFY connection becomes healthy, -1 on disconnect.</summary>
  public UpDownCounter<int> ConnectionState { get; }

  /// <summary>Mode-decision counter (startup and runtime transitions), tagged by <c>mode</c>
  /// and <c>reason</c>.</summary>
  public Counter<long> SignalingMode { get; }

  /// <summary>Initializes a new instance of the <see cref="NotifyMetrics"/> class.</summary>
  public NotifyMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    SignalsReceived = meter.CreateCounter<long>(
      "whizbang.postgres.notifications.signals_received",
      description: "NOTIFY signals delivered to subscribers, tagged by category (outbox/inbox/perspective/unknown)");

    ConnectionState = meter.CreateUpDownCounter<int>(
      "whizbang.postgres.notifications.connection_state",
      description: "+1 when LISTEN/NOTIFY is healthy, -1 on disconnect; sum=number of connected pods");

    SignalingMode = meter.CreateCounter<long>(
      "whizbang.postgres.notifications.signaling_mode",
      description: "Mode-decision events (startup + runtime transitions), tagged by mode and reason");
  }
}
