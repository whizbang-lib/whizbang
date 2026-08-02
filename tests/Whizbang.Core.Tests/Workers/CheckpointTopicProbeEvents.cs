using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Workers.CheckpointTopicProbes.Orders {
  /// <summary>Probe event in its own namespace — resolves to the "…orders" domain topic.</summary>
  public sealed record OrdersProbeEvent : IEvent {
    /// <summary>Stream identity (unused; satisfies WHIZ009 for the routing probe).</summary>
    [StreamId]
    public Guid StreamId { get; init; }
  }
}

namespace Whizbang.Core.Tests.Workers.CheckpointTopicProbes.Users {
  /// <summary>Probe event in a second namespace — resolves to the "…users" domain topic.</summary>
  public sealed record UsersProbeEvent : IEvent {
    /// <summary>Stream identity (unused; satisfies WHIZ009 for the routing probe).</summary>
    [StreamId]
    public Guid StreamId { get; init; }
  }
}
