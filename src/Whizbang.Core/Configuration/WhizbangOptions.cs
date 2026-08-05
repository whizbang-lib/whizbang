namespace Whizbang.Core.Configuration;

/// <summary>
/// Configuration options for Whizbang runtime behavior.
/// </summary>
public class WhizbangOptions {
  /// <summary>
  /// When true, TrackedGuid validation is disabled project-wide.
  /// Methods accept raw Guid without tracking metadata validation.
  /// Default: false
  /// </summary>
  public bool DisableGuidTracking { get; set; }

  /// <summary>
  /// Severity level for time-ordering violations in IDs.
  /// Default: Warning
  /// </summary>
  public GuidOrderingSeverity GuidOrderingViolationSeverity { get; set; } = GuidOrderingSeverity.Warning;

  /// <summary>
  /// When true, the Whizbang ASCII art banner is displayed on service startup.
  /// Default: true
  /// </summary>
  public bool ShowBanner { get; set; } = true;

  /// <summary>
  /// When true, Whizbang will automatically generate a StreamId for events that implement
  /// IHasStreamId when their StreamId is Guid.Empty. This prevents events from being stored
  /// with empty StreamIds, which can cause perspective worker issues.
  /// Default: true
  /// </summary>
  /// <docs>fundamentals/events/stream-id#auto-generation</docs>
  public bool AutoGenerateStreamIds { get; set; } = true;

  /// <summary>
  /// Guardrail configuration: opt-in tracking / enforcement for receptor invocations,
  /// designed to detect and prevent a receptor from firing more than once per message.
  /// </summary>
  /// <docs>fundamentals/receptors/exactly-once-firing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvocationTrackingTests.cs:TrackOnlyModeRecordsButDoesNotEnforceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvocationTrackingTests.cs:OnDoubleFireThrowRaisesDuplicateReceptorFireExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ChaosInjectorInvokerTests.cs:IsActive_FlagOn_WithInjector_TrueAsync</tests>
  public WhizbangGuardrailsOptions Guardrails { get; set; } = new();
}

/// <summary>
/// Guardrail configuration for <see cref="Messaging.ReceptorInvoker"/>. The receptor firing
/// contract is "exactly once per receptor in the lifetime of a message, unless the receptor
/// opts in to idempotency via <c>[ReceptorIdempotent]</c>". These options control how that
/// contract is tracked and enforced.
/// </summary>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvocationTrackingTests.cs:OffModeDoesNotRecordOrEnforceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvocationTrackingTests.cs:OnDoubleFireThrowRaisesDuplicateReceptorFireExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ChaosInjectorInvokerTests.cs:IsActive_FlagOn_WithInjector_TrueAsync</tests>
public class WhizbangGuardrailsOptions {
  /// <summary>
  /// Controls whether receptor invocations are recorded on the envelope and whether a
  /// prior record blocks a later attempt. <see cref="ReceptorInvocationTracking.TrackAndEnforce"/>
  /// (default) both records and enforces. <see cref="ReceptorInvocationTracking.Track"/>
  /// records but does not skip — useful during rollout to gather data before flipping the
  /// enforcement on. <see cref="ReceptorInvocationTracking.Off"/> disables both.
  /// </summary>
  public ReceptorInvocationTracking ReceptorInvocationTracking { get; set; } = ReceptorInvocationTracking.TrackAndEnforce;

  /// <summary>
  /// What to do when enforcement is on and a duplicate invocation is detected.
  /// <see cref="DoubleFireBehavior.Warn"/> (default) emits a Warning log and skips the
  /// receptor — observable via log-based alerting, non-fatal, and does not backpressure
  /// legitimate retry flows. <see cref="DoubleFireBehavior.Throw"/> raises
  /// <see cref="Messaging.DuplicateReceptorFireException"/> from the invoker — useful in
  /// canary or pre-prod environments where any duplicate should halt processing.
  /// </summary>
  public DoubleFireBehavior OnDoubleFire { get; set; } = DoubleFireBehavior.Warn;

  /// <summary>
  /// Where invocation records are persisted. <see cref="InvocationPersistence.Envelope"/>
  /// (default) writes records to <see cref="Observability.IMessageEnvelope.ReceptorInvocations"/>,
  /// surviving transport and inbox / outbox serialization with zero DB writes on the hot path.
  /// <see cref="InvocationPersistence.Database"/> is reserved for a future database-backed
  /// implementation of <see cref="Messaging.IReceptorDedupStore"/>; not shipped today. Pick
  /// <c>Envelope</c> unless a consumer has wired a custom DB-backed store.
  /// </summary>
  public InvocationPersistence PersistInvocations { get; set; } = InvocationPersistence.Envelope;

  /// <summary>
  /// When true, framework workers (<c>PerspectiveWorker</c>, <c>TransportConsumerWorker</c>,
  /// outbox drain, inbox commit) call into <see cref="Messaging.IChaosInjector"/> at named
  /// checkpoints. Default is false — production pays zero cost. Test projects flip this on
  /// and register an <c>IChaosInjector</c> to deterministically inject faults at boundaries
  /// that would otherwise require real crashes or external timing manipulation to exercise.
  /// </summary>
  public bool EnableChaosHooks { get; set; }
}

/// <summary>
/// Controls the <see cref="Messaging.ReceptorInvoker"/> double-fire guardrail's record +
/// enforce behavior.
/// </summary>
public enum ReceptorInvocationTracking {
  /// <summary>No tracking — receptors fire without consulting the dedup store, and no records are written.</summary>
  Off,
  /// <summary>Record invocations but do not skip on duplicates. Useful for observability-only rollout.</summary>
  Track,
  /// <summary>Record invocations and skip / throw on duplicates (per <see cref="WhizbangGuardrailsOptions.OnDoubleFire"/>).</summary>
  TrackAndEnforce
}

/// <summary>
/// Controls how <see cref="Messaging.ReceptorInvoker"/> reacts when a duplicate receptor
/// invocation is detected.
/// </summary>
public enum DoubleFireBehavior {
  /// <summary>Emit a Warning log and skip the receptor.</summary>
  Warn,
  /// <summary>Throw <see cref="Messaging.DuplicateReceptorFireException"/> from <see cref="Messaging.IReceptorInvoker.InvokeAsync"/>.</summary>
  Throw
}

/// <summary>
/// Controls which <see cref="Messaging.IReceptorDedupStore"/> backing is used.
/// </summary>
public enum InvocationPersistence {
  /// <summary>Default. Records are written to the envelope's <see cref="Observability.IMessageEnvelope.ReceptorInvocations"/> list.</summary>
  Envelope,
  /// <summary>Reserved for a future database-backed implementation; not shipped today.</summary>
  Database
}

/// <summary>
/// Severity levels for GUID ordering validation violations.
/// </summary>
public enum GuidOrderingSeverity {
  /// <summary>
  /// Suppress all validation messages.
  /// </summary>
  None,

  /// <summary>
  /// Log at Info level.
  /// </summary>
  Info,

  /// <summary>
  /// Log at Warning level (default).
  /// </summary>
  Warning,

  /// <summary>
  /// Log at Error level and throw exception.
  /// </summary>
  Error
}
