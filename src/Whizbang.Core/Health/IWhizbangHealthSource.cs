namespace Whizbang.Core.Health;

/// <summary>
/// A health hook for one Whizbang-managed resource (the event-store/DB, transport, offload store,
/// worker pipeline, schema initializer, signal bus, temporal engine, …). Each provider implements
/// this to report <em>its own</em> raw state — only it can tell whether it is genuinely broken. The
/// framework (<see cref="WhizbangHealthAggregator"/>) overlays lifecycle + policy to produce the
/// effective liveness/readiness answer, so consumers never hand-roll a naive check against
/// Whizbang's internal tables.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthDiTests.cs:AddWhizbangManagedHealth_AggregatesRegisteredSourcesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/TransportHealthWiringTests.cs:RegisteredTransport_Disconnected_WhileMigrating_PausedByDesignAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/OffloadHealthWiringTests.cs:RegisteredStore_Unreachable_WhileRunning_FaultedAsync</tests>
public interface IWhizbangHealthSource {
  /// <summary>
  /// Stable component id used to key per-component policy overrides, e.g. <c>"event-store"</c>,
  /// <c>"transport"</c>, <c>"offload"</c>, <c>"workers"</c>, <c>"schema"</c>.
  /// </summary>
  string Component { get; }

  /// <summary>Reports this resource's current state. Must not throw for an intentional state.</summary>
  ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken);
}
