namespace Whizbang.Core.Configuration;

/// <summary>
/// Options for ephemeral-events runtime behavior — currently how the startup type-definition reconciler
/// handles historical drift when a type's settings change in code (e.g. it gains <c>[Ephemeral]</c>).
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public sealed class EphemeralOptions {
  /// <summary>
  /// When <c>true</c>, the startup reconciler ACTS on detected settings drift — e.g. reclassifies a type's
  /// historical Sourced events to Ephemeral (stamp + offload so the reaper can reclaim them). Default
  /// <c>false</c>: drift is only detected and reported (logged + a lineage edge recorded), because
  /// rewriting and reaping historical events on deploy is a deliberate, potentially lossy operation and
  /// keeping the pre-existing history is sometimes the intent. Flip to true, or run the reclassify command
  /// explicitly, to act.
  /// </summary>
  public bool ReconcileHistoricalOnStartup { get; set; }
}
