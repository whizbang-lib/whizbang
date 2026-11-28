// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Rewinds the rows a fan-out reached late (#2622): after the collective sink applies a foreign event to the rows whose key
/// holds its stream, each of them that had already folded an own event committed after the foreign event is rewound, so
/// its replay folds the two in commit order (#2621) and the live row comes out as its rebuild. The sink calls it once per
/// fan-out of the event's type after the event's apply commits; a driver that applies fan-outs registers it.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.FanOut.cs:CollectiveSink_AFanOutItApplies_AsksEachFanOutOfItsTypeToRewindTheRowsItReachedLateAsync</tests>
public interface IFanOutLateApplyRewinds {
  /// <summary>
  /// Rewinds the rows of <paramref name="fanOut"/>'s model in <paramref name="tenantId"/> whose key holds
  /// <paramref name="streamId"/> and that folded an own event committed after <paramref name="eventId"/>.
  /// </summary>
  /// <param name="fanOut">The fan-out the event was applied through.</param>
  /// <param name="streamId">The foreign event's stream: the key value of the rows it targets.</param>
  /// <param name="tenantId">The foreign event's tenant.</param>
  /// <param name="eventId">The foreign event the sink applied.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The rows newly flagged for a rewind.</returns>
  Task<int> RewindRowsAppliedLateAsync(
    PerspectiveFanOutEntry fanOut, Guid streamId, string tenantId, Guid eventId, CancellationToken cancellationToken);
}
