// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Workers.BackfillProbes.Orders {
  /// <summary>Consumed probe event in one owner's domain, for the targeted backfill tests.</summary>
  public sealed record PlacedEvent : IEvent {
    /// <summary>Stream identity (unused; satisfies WHIZ009).</summary>
    [StreamId]
    public Guid StreamId { get; init; }
  }
}

namespace Whizbang.Core.Tests.Workers.BackfillProbes.Billing {
  /// <summary>Consumed probe event in a second owner's domain, for the targeted backfill tests.</summary>
  public sealed record BilledEvent : IEvent {
    /// <summary>Stream identity (unused; satisfies WHIZ009).</summary>
    [StreamId]
    public Guid StreamId { get; init; }
  }
}
