// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A foreign event a perspective fans out (<see cref="IPerspectiveFanOutFor{TModel, TEvent}"/>), as the collective sink
/// hands it to the collective dispatcher (#2620): the event, and a <see cref="FanOutScope"/> of its stream and tenant.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper lets a fan-out run through the collective machinery as it stands: the dispatcher, the driver's
/// keyset-batched <c>UPDATE</c>, the apply lock, the resumable progress record (#1969) and the reach record (#1970).
/// Only fan-out entries apply it (<see cref="CollectiveApplyEntry.AppliesTo"/>), and <see cref="FanOutScopeResolver"/>
/// turns its scope into the rows' filter.
/// </para>
/// <para>
/// It is built on the receiver from a stored event and never serialized: it carries no ordering key and no predecessor,
/// so the sink applies fan-outs in the order it reads them.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:FanOutCollectiveEvent_ItsScope_IsTheStreamAndTenant_IdentifiedByBothAsync</tests>
// WHIZ110 asks for a pinned identity so a stored type survives renames. This type is never stored, sent or
// serialized: the sink builds it in memory around a stored event and hands it to the dispatcher.
#pragma warning disable WHIZ110
internal sealed class FanOutCollectiveEvent : ICollectiveEvent {
#pragma warning restore WHIZ110
  /// <summary>Wraps a foreign event for the collective dispatcher.</summary>
  /// <param name="event">The foreign event, as stored.</param>
  /// <param name="streamId">The event's stream: the key value of the rows it targets.</param>
  /// <param name="tenantId">The event's tenant: the only tenant whose rows it targets.</param>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:FanOutCollectiveEvent_ANullEventOrTenant_ThrowsAsync</tests>
  public FanOutCollectiveEvent(IEvent @event, Guid streamId, string tenantId) {
    ArgumentNullException.ThrowIfNull(@event);
    ArgumentNullException.ThrowIfNull(tenantId);
    Event = @event;
    Scope = new FanOutScope(streamId, tenantId, @event.GetType());
  }

  /// <summary>The foreign event, passed to the perspective's fan-out <c>Apply</c>.</summary>
  public IEvent Event { get; }

  /// <inheritdoc />
  public CollectiveScope Scope { get; }
}

/// <summary>
/// The scope of a <see cref="FanOutCollectiveEvent"/>: the rows whose key column holds <see cref="StreamId"/>, in
/// <see cref="TenantId"/>, for the fan-out of <see cref="EventType"/>. Never serialized.
/// </summary>
/// <param name="StreamId">The foreign event's stream id: the key value the rows hold.</param>
/// <param name="TenantId">The foreign event's tenant.</param>
/// <param name="EventType">The foreign event's type, which picks each model's key.</param>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:FanOutCollectiveEvent_ItsScope_IsTheStreamAndTenant_IdentifiedByBothAsync</tests>
public sealed record FanOutScope(Guid StreamId, string TenantId, Type EventType) : CollectiveScope {
  /// <summary>The scope kind <see cref="FanOutScopeResolver"/> handles.</summary>
  public const string KIND = "fan-out";

  /// <inheritdoc />
  public override string ScopeKind => KIND;

  /// <summary>
  /// The tenant and the key: the apply lock is taken per table and this identity, so fan-outs of two keys run at once
  /// and two of one key take turns.
  /// </summary>
  public override string ScopeIdentity => KIND + ":" + TenantId + ":" + StreamId.ToString("D");
}
