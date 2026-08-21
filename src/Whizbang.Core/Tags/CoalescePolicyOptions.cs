using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tags;

/// <summary>
/// Per-tag coalesce policy: the knobs for the sliding-window batcher that folds tagged outbox
/// singles into composite envelopes. Bound to a tag string via
/// <see cref="TagOptions.Coalesce(string, Action{CoalescePolicyOptions})"/> — tags classify,
/// policies bind; message types never declare batching themselves.
/// </summary>
/// <remarks>
/// <para>
/// The defaults mirror the audit-ship knobs
/// (<see cref="SystemEvents.SystemEventOptions.AuditShipSlideSeconds"/> /
/// <c>AuditShipMaxDelaySeconds</c> / <c>AuditShipMaxBatchCount</c>, i.e. 15 / 120 / 500, with
/// <see cref="FanoutAtomicity.Independent"/> fan-out) so the built-in audit binding and a
/// host-declared binding describe the same shipped behavior.
/// </para>
/// <para>
/// <see cref="SlideSeconds"/> = 0 disables the group entirely: no coalesce-group stamp, no
/// <c>ScheduledFor</c> floor — every tagged single ships immediately and individually.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/CoalescePolicyOptionsTests.cs:Defaults_MirrorTheAuditShipKnobsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/CoalescePolicyOptionsTests.cs:SlideSecondsZero_IsTheDisabledSentinelAsync</tests>
public sealed class CoalescePolicyOptions {
  /// <summary>
  /// Quiet window (seconds): pending singles for the group are folded once no new singles have
  /// arrived for this long, so a burst's entire tail coalesces and ships at burst-end.
  /// <c>0</c> disables the group (immediate individual shipping, no floor). Default: 15.
  /// </summary>
  public int SlideSeconds { get; set; } = 15;

  /// <summary>
  /// Hard freshness cap (seconds). Singles are minted with
  /// <c>ScheduledFor = now + MaxDelaySeconds</c> — the safety floor that keeps them invisible to
  /// the normal claim pump while pending, and the bound on how long the batcher may keep sliding
  /// under continuous arrivals (oldest-first ship when exceeded). Default: 120.
  /// </summary>
  public int MaxDelaySeconds { get; set; } = 120;

  /// <summary>
  /// Maximum singles folded into one composite envelope; a larger pending set splits into
  /// multiple composites. Default: 500.
  /// </summary>
  public int MaxBatchCount { get; set; } = 500;

  /// <summary>
  /// Per-child failure policy for the shipped composite. Default
  /// <see cref="FanoutAtomicity.Independent"/>: coalesce groups bundle self-contained records,
  /// so one poison inner dead-letters alone. <see cref="FanoutAtomicity.Atomic"/> is opt-in for
  /// groups that genuinely want all-or-nothing.
  /// </summary>
  public FanoutAtomicity Atomicity { get; set; } = FanoutAtomicity.Independent;

  /// <summary>
  /// Builds the composite the coalesce shipper folds a batch of pending singles into.
  /// Null (default) uses the generic raw-carry <see cref="CoalescedEventsComposite"/>; the
  /// built-in audit binding supplies <c>AuditEventsComposite</c> through this seam. Factories
  /// are plain code — this is what keeps composite construction AOT-safe (no reflection over
  /// composite types at fold time).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_BindingFactory_BuildsTheBindingsCompositeAsync</tests>
  public Func<CoalesceFoldBatch, CompositeEventBase>? CompositeFactory { get; set; }
}
