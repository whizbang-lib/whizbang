// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The message types of the framework's steady-state integrity traffic: the checkpoint every peer publishes on its own
/// cadence, the redelivery request a checkpoint can set off, and the redelivery that answers it.
/// </summary>
/// <remarks>
/// They flow whether or not the service has any application work, as background-band rows, so the housekeeping idle gate
/// does not count them as work (<see cref="ServiceBacklog.IsQuiescentApartFromIntegrity"/>, #2253). Integrity already
/// outranks maintenance through the housekeeping coordinator's slots, so it does not also need to hold maintenance back
/// through the backlog reading.
/// </remarks>
/// <docs>operations/workers/housekeeping-arbitration#what-counts-as-work</docs>
public static class SteadyIntegrityTraffic {
  /// <summary>The types, in no particular order.</summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SteadyIntegrityTrafficTests.cs:TheTraffic_IsTheCheckpoint_TheRedeliveryRequest_AndItsAnswerAsync</tests>
  public static IReadOnlyList<Type> Types { get; } =
    [typeof(IntegrityCheckpoint), typeof(RequestRedeliveryCommand), typeof(Minting.RedeliveryComposite)];

  /// <summary>The stored form of each type (<see cref="TypeNameFormatter.Format"/>), which the backlog reading compares
  /// with the normalized <c>message_type</c> of each work row.</summary>
  public static IReadOnlyList<string> StoredNames { get; } = [.. Types.Select(TypeNameFormatter.Format)];
}
