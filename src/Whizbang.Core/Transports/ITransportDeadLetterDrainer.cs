namespace Whizbang.Core.Transports;

/// <summary>
/// Recovers messages that landed in a transport broker's own dead-letter queue (e.g. Azure
/// Service Bus' <c>$DeadLetterQueue</c> subscription, RabbitMQ's DLX-bound queue) by re-
/// emitting them onto the normal receive path. Distinct from <see cref="Whizbang.Core.Messaging.IDeadLetterStore"/>,
/// which manages Whizbang's internal <c>wh_dead_letters</c> table — that flow is policy-driven
/// and per-row; this one is transport-aggressive and runs on a backstop interval.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang's event store is source-of-truth, so messages stuck in a broker DLQ are
/// <em>operational noise</em>, not data loss — the underlying events still replay
/// from <c>wh_event_store</c> via the normal claim path. But operators don't want
/// the broker DLQ to grow unbounded, and they don't want to drop into psql or the
/// Azure portal to drain it. This abstraction lets the framework do that automatically.
/// </para>
/// <para>
/// Implementations: <see cref="Whizbang.Transports.AzureServiceBus.AzureServiceBusDeadLetterDrainer"/>
/// for ASB Subscribe + Poll modes; <see cref="Whizbang.Transports.RabbitMQ.RabbitMqDeadLetterDrainer"/>
/// for RMQ. Each gets DI-registered in its hosting extension; <see cref="Whizbang.Core.Workers.TransportDeadLetterDrainWorker"/>
/// resolves all of them on every tick.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs:DrainersInvoked_AllReturnNonZero_TotalAccumulatedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs:OneDrainerThrows_OthersStillRunAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs:TransportNames_ExposedForMetricsDimensionAsync</tests>
public interface ITransportDeadLetterDrainer {
  /// <summary>
  /// Short transport identifier — appears as a metric dimension and a log scope. Examples:
  /// <c>asb</c>, <c>rmq</c>. One drainer per (transport-kind × subscription/queue) pair.
  /// </summary>
  string TransportName { get; }

  /// <summary>
  /// Drains up to <paramref name="maxCount"/> messages from the broker's dead-letter queue,
  /// re-submitting each one onto the normal receive path before settling it off the DLQ.
  /// Returns the number of messages re-submitted. Stops early if the DLQ is empty, the
  /// cap is hit, or <paramref name="ct"/> is canceled.
  /// </summary>
  /// <remarks>
  /// Implementations MUST be idempotent under concurrent execution — the worker may invoke
  /// this on a cadence even while a previous tick is still draining. Use broker-side locks /
  /// receive-once semantics rather than C# guards (which can't span pods).
  /// </remarks>
  Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default);
}
