using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Translates the audit-ship knobs on <see cref="SystemEventOptions"/> into the built-in
/// <c>Coalesce(SystemTags.AUDIT)</c> binding: with audit enabled and
/// <see cref="SystemEventOptions.AuditShipSlideSeconds"/> &gt; 0, the sys-audit group ships via
/// the generic coalesce machinery, folded into <see cref="AuditEventsComposite"/>. Registered
/// with add-if-absent semantics ("registered FIRST"), so a host
/// <c>options.Tags.Coalesce(SystemTags.AUDIT, ...)</c> binding replaces it entirely regardless
/// of registration order. Slide = 0 registers nothing — no group, no floor, today's immediate
/// per-event shipping.
/// </summary>
/// <docs>fundamentals/security/audit-logging#audit-shipping</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditCoalesceRebaseTests.cs:Apply_AuditEnabled_RegistersTheBuiltInBindingFromTheKnobsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditCoalesceRebaseTests.cs:Apply_SlideZero_RegistersNothingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditCoalesceRebaseTests.cs:Apply_HostBindingForSysAudit_AlwaysWinsAsync</tests>
public static class SystemEventCoalesceDefaults {
  /// <summary>
  /// Applies the built-in audit coalesce binding to <paramref name="tagOptions"/> when
  /// <paramref name="systemEventOptions"/> has audit enabled with a non-zero slide. Idempotent
  /// and add-if-absent: a host binding for the same tag always survives.
  /// </summary>
  /// <param name="tagOptions">The tag options to receive the built-in binding.</param>
  /// <param name="systemEventOptions">The system event options carrying the audit-ship knobs; null = nothing to apply.</param>
  public static void Apply(TagOptions tagOptions, SystemEventOptions? systemEventOptions) {
    ArgumentNullException.ThrowIfNull(tagOptions);
    if (systemEventOptions is not { AuditEnabled: true } || systemEventOptions.AuditShipSlideSeconds <= 0) {
      return;
    }

    tagOptions.UseCoalesceBindingDefault(SystemTags.AUDIT, new CoalescePolicyOptions {
      SlideSeconds = systemEventOptions.AuditShipSlideSeconds,
      MaxDelaySeconds = systemEventOptions.AuditShipMaxDelaySeconds,
      MaxBatchCount = systemEventOptions.AuditShipMaxBatchCount,
      Atomicity = FanoutAtomicity.Independent,
      // The audit group folds into its proven carrier (identity preservation + raw carry,
      // fan-out locked by CompositeInboxFanoutTests) instead of the generic composite.
      CompositeFactory = BuildAuditComposite
    });
  }

  /// <summary>
  /// The audit group's composite factory: <see cref="AuditEventsComposite"/> raw-carrying each
  /// folded single's stored payload, wire type name, and ORIGINAL message id.
  /// </summary>
  /// <param name="batch">The fold batch.</param>
  /// <returns>The built audit composite.</returns>
  public static CompositeEventBase BuildAuditComposite(CoalesceFoldBatch batch) {
    ArgumentNullException.ThrowIfNull(batch);
    return new AuditEventsComposite {
      StreamId = TrackedGuid.NewMedo(),
      Atomicity = batch.Atomicity,
      InnerPayloads = [.. batch.Singles.Select(m => m.Envelope.Payload)],
      InnerTypeNames = [.. batch.Singles.Select(m => m.MessageType)],
      InnerEventIds = [.. batch.Singles.Select(m => m.MessageId)],
      // #596: each single's OWN stream rides the wire, so the receiver's expansion restores
      // per-stream identity instead of collapsing every child onto the composite's stream.
      InnerStreamIds = [.. batch.Singles.Select(m => m.StreamId ?? Guid.Empty)]
    };
  }
}
