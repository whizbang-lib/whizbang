// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Events.System;

/// <summary>
/// The retention of the framework's own rewind and rebuild events (#2320): every type that implements
/// <see cref="IEphemeralSystemEvent"/> is ephemeral, kept for <see cref="DEFAULT_TTL_SECONDS"/> unless
/// <c>Whizbang:Ephemeral:SystemEvents:Ttl</c> says otherwise.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#framework-system-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Events/System/SystemEventRetentionTests.cs</tests>
public static class SystemEventRetention {
  /// <summary>The default retention of a rewind or rebuild event: 30 days, in seconds.</summary>
  public const int DEFAULT_TTL_SECONDS = 30 * 24 * 60 * 60;

  /// <summary>
  /// The ephemeral mode a type has once the configured retention is applied: for a framework system event
  /// (<see cref="IEphemeralSystemEvent"/>) both its TTL and its per-type grace become <paramref name="ttl"/>, so the
  /// reaper keeps a new event and a reclassified historical one alike until that long after it was written; any other
  /// type keeps its declared mode.
  /// </summary>
  /// <param name="type">The event's CLR type.</param>
  /// <param name="info">The mode the type declares.</param>
  /// <param name="ttl">The configured retention.</param>
  /// <returns>The mode to use.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Events/System/SystemEventRetentionTests.cs:Apply_ToASystemEvent_SetsTtlAndGrace_AndLeavesOtherTypesAloneAsync</tests>
  public static EphemeralInfo Apply(Type type, EphemeralInfo info, TimeSpan ttl) {
    if (!typeof(IEphemeralSystemEvent).IsAssignableFrom(type)) {
      return info;
    }
    var seconds = (int)ttl.TotalSeconds;
    return info with { TtlSeconds = seconds, RewindGraceSeconds = seconds };
  }
}
