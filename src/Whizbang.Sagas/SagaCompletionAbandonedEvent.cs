namespace Whizbang.Sagas;

/// <summary>
/// Concrete abandon event the framework emits from
/// <c>BaseSagaService.TryRecoverViaWatchdogTickAsync</c> when the watchdog
/// has exhausted its re-arm budget without observing completion. Consumers
/// react via a receptor on this type (or <see cref="ISagaCompletionAbandonedEvent"/>).
/// </summary>
/// <remarks>
/// <para>
/// Concrete (not abstract) so the framework can construct it directly without
/// requiring consumers to supply a per-saga subclass. Implements
/// <see cref="ISagaCompletionAbandonedEvent"/> so receptors discovered by type
/// pick it up; extends <see cref="SagaEventBase"/> to inherit
/// <see cref="Whizbang.Core.IEvent"/> implementation. Carrying
/// <see cref="SagaName"/> + <see cref="EntityId"/> on every instance keeps it
/// shape-compatible with the rest of the saga lifecycle, which means the
/// existing message-registry routing handles it without a special case.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonsAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:Complete_RecoversWithoutReArmAsync</tests>
public class SagaCompletionAbandonedEvent : SagaEventBase, ISagaCompletionAbandonedEvent {

  /// <summary>The name of the saga whose completion was abandoned.</summary>
  public string SagaName { get; set; } = string.Empty;

  /// <summary>The saga's entity id — the business entity the saga was initiated for.</summary>
  public Guid EntityId { get; set; }

  /// <summary>Stream id this abandon event is bound to (the saga's stream).</summary>
  public Guid StreamId { get; set; }

  /// <summary>
  /// How many watchdog ticks were attempted before the abandon — equals
  /// the size of <c>SagaOptions.WatchdogBackoff</c> at abandon time.
  /// </summary>
  public int RescheduleCount { get; set; }
}
