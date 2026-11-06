// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Sends one <see cref="RequestRedeliveryCommand"/> to an origin service: the directed control-plane envelope, on
/// the origin's request topic, keyed by the request's first stream. Shared by the paced repair drain and the
/// operator's <see cref="IStreamRedeliveryRequester"/>, so a request an operator makes reaches the origin exactly as
/// the ledger's do.
/// </summary>
/// <docs>resilience/stream-integrity#operator-redelivery</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/RepairDrainWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamRedeliveryRequesterTests.cs</tests>
internal static class RedeliveryRequestDispatch {
  /// <summary>
  /// Publishes <paramref name="command"/> to <paramref name="originServiceName"/> (null broadcasts, #2092) at <paramref name="destination"/>: the
  /// origin's request topic, keyed by one of the requested streams and carrying the integrity time to live (#2078, built
  /// by <see cref="IntegrityControlDestination"/>).
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "One directed send: the transport and serializer, the sender's identity, the command, its addressee and destination, the clock and cancellation. Every caller holds each separately.")]
  internal static async Task PublishAsync(
      ITransport transport, IEnvelopeSerializer serializer, IServiceInstanceProvider? instanceProvider,
      RequestRedeliveryCommand command, string? originServiceName, TransportDestination destination,
      DateTimeOffset now, CancellationToken cancellationToken) {
    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = command,
      Hops = [ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), instanceProvider, now)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = originServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      destination,
      serialized.EnvelopeType, cancellationToken: cancellationToken).ConfigureAwait(false);
  }
}
