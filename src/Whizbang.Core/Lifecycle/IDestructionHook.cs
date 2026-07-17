namespace Whizbang.Core.Lifecycle;

/// <summary>
/// A developer hook the reaper awaits around an ephemeral destruction (E2). Register one in DI to run logic
/// on the reaper's critical path — snapshot, compact, archive, crypto-shred, emit an ephemeral event, or
/// decide the destruction's fate — <em>before</em> the body is physically deleted. Optional: with no
/// <see cref="IDestructionHook"/> registered, the reaper's blunt consumption-gated delete runs unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering.</strong> <see cref="OnBeforeDestructionAsync"/> is awaited BEFORE the physical reap, so
/// any preserve-work it commits (a snapshot, a carry-forward summary, an archive write) is durable before
/// the bytes go. <see cref="OnAfterDestructionAsync"/> runs detached AFTER the delete commits (notify /
/// metrics / cascade).
/// </para>
/// <para>
/// <strong>Scope of this increment (E2-2).</strong> The hook fires and can preserve data; the returned
/// <see cref="DestructionResult"/> is captured and logged. <see cref="DestructionResult.Cancel"/> /
/// <see cref="DestructionResult.DeferUntil"/> <em>enforcement</em> (holding the body from the reap /
/// rescheduling it) lands in the next increment via a SQL hold-gate; the retry-on-failure policy lands after
/// that. Until then, a hook is for preserve-work; treat Cancel/Defer as advisory.
/// </para>
/// <para>The ephemeral→Sourced boundary stays closed: a hook may emit ephemeral events and produce an
/// authoritative ephemeral summary, but never re-emit the ephemeral payload as a durable Sourced event.</para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
public interface IDestructionHook {
  /// <summary>
  /// Awaited on the reaper's critical path, before the physical delete. Do preserve-work here (it must
  /// durably commit before returning) and return a <see cref="DestructionResult"/> deciding the fate.
  /// </summary>
  ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default);

  /// <summary>
  /// Runs detached after the physical delete has committed — for notifications, metrics, or cascades.
  /// </summary>
  ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default);
}
