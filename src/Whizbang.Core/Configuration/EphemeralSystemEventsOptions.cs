// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// How long the framework's own rewind and rebuild events are kept, under <c>Whizbang:Ephemeral:SystemEvents</c>
/// (#2320). These events (<c>PerspectiveRebuildStarted</c>, <c>PerspectiveRebuildProgress</c>,
/// <c>PerspectiveRebuildCompleted</c>, <c>PerspectiveRebuildFailed</c>, <c>PerspectiveRewindStarted</c> and
/// <c>PerspectiveRewindCompleted</c>) are operational bookkeeping that nothing rebuilds state from, each written to a
/// stream of its own, so they are ephemeral and their bodies are reaped once this long has passed since they were
/// written.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#framework-system-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Events/System/SystemEventRetentionTests.cs:SystemEventsTtl_BindsFromConfigurationAsync</tests>
public sealed class EphemeralSystemEventsOptions {
  /// <summary>
  /// How long a rewind or rebuild event is kept after it is written, under <c>Whizbang:Ephemeral:SystemEvents:Ttl</c>
  /// (for example <c>30.00:00:00</c>). Default 30 days. Must be greater than zero and fit in whole seconds as a 32-bit
  /// integer. It applies to events already stored too once the history walk
  /// (<see cref="EphemeralOptions.ReconcileHistoricalOnStartup"/>) has reclassified them, because the reaper measures it
  /// from each event's stored creation time.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events#framework-system-events</docs>
  /// <tests>tests/Whizbang.Core.Tests/Events/System/SystemEventRetentionTests.cs:SystemEventsTtl_DefaultsToThirtyDaysAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Events/System/SystemEventRetentionTests.cs:SystemEventsTtl_BindsFromConfigurationAsync</tests>
  public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(Whizbang.Core.Events.System.SystemEventRetention.DEFAULT_TTL_SECONDS);
}
