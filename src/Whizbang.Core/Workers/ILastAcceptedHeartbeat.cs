// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// When this instance's last heartbeat was accepted (#2126): the exit's account of how long the instance had gone without
/// one, which is what to look at first when an eviction was not expected. The heartbeat worker is the one source.
/// </summary>
/// <docs>operations/startup/instance-exits#the-log-entry</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs:TheEntry_CarriesEveryRequiredFact_ForAnEvictionAsync</tests>
public interface ILastAcceptedHeartbeat {
  /// <summary>When the most recent beat was accepted, or null before the first.</summary>
  DateTimeOffset? LastAcceptedAt { get; }
}
