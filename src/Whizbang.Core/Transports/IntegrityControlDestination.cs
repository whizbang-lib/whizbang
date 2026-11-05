// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Transports;

/// <summary>
/// The destination every directed stream-integrity control message is published to (#2078): the control-plane
/// destination (session key and conventional Subject) stamped with the integrity time to live, so a stale request,
/// manifest or answer expires at the broker instead of accumulating on a stopped service's subscription.
/// </summary>
/// <docs>resilience/stream-integrity#directed-integrity-traffic</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/IntegrityControlDestinationTests.cs</tests>
public static class IntegrityControlDestination {
  /// <summary>A destination for <paramref name="address"/>, keyed by <paramref name="sessionStreamId"/>, expiring
  /// after <see cref="StreamIntegrityOptions.ControlMessageTimeToLiveMinutes"/>.</summary>
  /// <param name="address">The addressee's request topic, or the requester's reply topic.</param>
  /// <param name="sessionStreamId">The session key.</param>
  /// <param name="payloadType">The control message type, for the Subject.</param>
  /// <param name="options">The stream-integrity options.</param>
  /// <returns>The destination.</returns>
  public static TransportDestination For(string address, Guid sessionStreamId, Type payloadType, StreamIntegrityOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return ControlPlaneDestination.For(address, sessionStreamId, payloadType);
  }
}
