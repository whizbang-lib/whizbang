using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Test-only hook invoked at named checkpoints inside the framework's hot paths so
/// integration tests can deterministically inject faults — process crashes, DB errors,
/// timeouts, or arbitrary delays — without resorting to timing hacks or reflection.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Guarded in production.</strong> Checkpoints are only invoked when
/// <see cref="Configuration.WhizbangGuardrailsOptions.EnableChaosHooks"/> is set to true
/// (default: false). Production code pays zero cost — the guard is a boolean read in the
/// worker's constructor, and when disabled no <see cref="IChaosInjector"/> reference is
/// even resolved.
/// </para>
/// <para>
/// <strong>Checkpoint names are stable strings.</strong> Workers expose well-known
/// checkpoint names (e.g., <c>"PerspectiveWorker.BeforeBatch"</c>,
/// <c>"TransportConsumerWorker.BeforeHandle"</c>). Tests key on these names to
/// selectively inject faults at the point they want to exercise.
/// </para>
/// <para>
/// <strong>Throwing from the injector is the point.</strong> A test that wants to
/// simulate "process crashed mid-outbox-drain" throws from the injector at
/// <c>"OutboxDrain.AfterPublish"</c>; the caller's exception handling is what the test
/// validates.
/// </para>
/// </remarks>
/// <docs>operations/testing/chaos-injection</docs>
public interface IChaosInjector {
  /// <summary>
  /// Invoked at a named checkpoint. Implementations may throw, delay, or record state
  /// depending on the test scenario.
  /// </summary>
  /// <param name="checkpoint">Stable checkpoint name (e.g., <c>"PerspectiveWorker.BeforeBatch"</c>).</param>
  /// <param name="payload">Checkpoint-specific context; contents vary per checkpoint.</param>
  /// <param name="cancellationToken">Cancellation from the caller.</param>
  ValueTask BeforeCheckpointAsync(string checkpoint, object? payload, CancellationToken cancellationToken);
}

/// <summary>
/// Well-known checkpoint names exposed by framework workers. Tests reference these
/// constants to avoid typos and get compile-time verification.
/// </summary>
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
public static class ChaosCheckpoints {
  /// <summary>Fires before a perspective worker begins processing a batch of events.</summary>
  public const string PERSPECTIVE_WORKER_BEFORE_BATCH = "PerspectiveWorker.BeforeBatch";
  /// <summary>Fires after a perspective worker finishes processing a batch of events.</summary>
  public const string PERSPECTIVE_WORKER_AFTER_BATCH = "PerspectiveWorker.AfterBatch";
  /// <summary>Fires before the perspective worker triggers the PostAllPerspectives / PostLifecycle completion receptors.</summary>
  public const string PERSPECTIVE_WORKER_BEFORE_COMPLETION_FIRE = "PerspectiveWorker.BeforeCompletionFire";
  /// <summary>Fires when a transport consumer worker is about to handle an inbound message.</summary>
  public const string TRANSPORT_CONSUMER_BEFORE_HANDLE = "TransportConsumerWorker.BeforeHandle";
  /// <summary>Fires after a transport consumer worker has handled an inbound message.</summary>
  public const string TRANSPORT_CONSUMER_AFTER_HANDLE = "TransportConsumerWorker.AfterHandle";
  /// <summary>Fires before the outbox drainer publishes a batch to the transport.</summary>
  public const string OUTBOX_DRAIN_BEFORE_PUBLISH = "OutboxDrain.BeforePublish";
  /// <summary>Fires after the outbox drainer successfully publishes a batch to the transport.</summary>
  public const string OUTBOX_DRAIN_AFTER_PUBLISH = "OutboxDrain.AfterPublish";
  /// <summary>Fires before the inbox commits a received message to the database.</summary>
  public const string INBOX_BEFORE_COMMIT = "Inbox.BeforeCommit";
  /// <summary>Fires after the inbox has committed a received message to the database.</summary>
  public const string INBOX_AFTER_COMMIT = "Inbox.AfterCommit";
}
#pragma warning restore CA1707
