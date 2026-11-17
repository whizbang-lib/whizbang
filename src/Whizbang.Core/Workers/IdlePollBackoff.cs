// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The wait of an integrity poller that ticks once a second while it has work (#2346): one second, doubling on each
/// idle tick up to <see cref="StreamIntegrityOptions.IdlePollCeilingSeconds"/>, back to one second on the first tick
/// that finds work. One rule for every such poller, so they cannot drift apart.
/// </summary>
/// <docs>resilience/stream-integrity#idle-pollers-back-off</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/RepairDrainWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SubscriptionBackfillWorkerTests.cs</tests>
internal static class IdlePollBackoff {
  /// <summary>The backoff for a one-second poller under <paramref name="options"/>.</summary>
  internal static AdaptiveIdleBackoff For(StreamIntegrityOptions options) =>
    new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(Math.Max(1, options.IdlePollCeilingSeconds)));
}
