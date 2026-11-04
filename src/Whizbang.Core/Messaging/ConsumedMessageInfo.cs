// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// One message type this service consumes: something registered here (an inbox receptor, a lifecycle receptor, a
/// perspective, a notification-tag consumer, the composite fan-out or the collective sink) accepts it at the receive
/// boundary. Enumerated by <see cref="IReceptorRegistryQuery.GetConsumedMessages"/> from the same generated data that
/// answers <see cref="IReceptorRegistryQuery.HasAnyConsumer"/>, so the list and the predicate cannot disagree.
/// </summary>
/// <param name="TypeName">The assembly-qualified wire form (<c>"Namespace.Outer+Type, Assembly"</c>), the form
/// <see cref="TypeNameFormatter.Format(System.Type)"/> writes and the event store's <c>event_type</c> column holds.</param>
/// <param name="IsEvent">True when the type is an <see cref="IEvent"/>: the types an origin stores and stream integrity
/// audits.</param>
/// <docs>internals/receptor-registry-query#what-a-service-consumes</docs>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQueryConsumedMessagesTests.cs</tests>
public sealed record ConsumedMessageInfo(string TypeName, bool IsEvent);
