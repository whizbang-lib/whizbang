// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// One stream-integrity delivery waiting for the maintainer's drain (#2001): a received integrity message, or a
/// committed redelivery bundle's reconcile, recorded where it arrived and handled later by the instance holding the
/// maintainer duty.
/// </summary>
/// <param name="DeliveryId">Derived from <paramref name="DeliveryType"/> and the payload bytes, so one delivery offered
/// several times is recorded once (<c>IntegrityDeliveryId</c>).</param>
/// <param name="DeliveryType">Which registered handler runs it (<c>IntegrityDeliveryRegistry</c>).</param>
/// <param name="Payload">The delivery as JSON, in the form its handler reads.</param>
/// <param name="Attempts">Failed attempts so far.</param>
/// <param name="QueuedAt">When it was recorded; the drain works in this order.</param>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs</tests>
public sealed record IntegrityDelivery(Guid DeliveryId, string DeliveryType, string Payload, int Attempts, DateTimeOffset QueuedAt);

/// <summary>What recording an integrity delivery did.</summary>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Enqueue_TheSameDeliveryTwice_RecordsItOnceAsync</tests>
public enum IntegrityDeliveryEnqueueResult {
  /// <summary>The store cannot record deliveries (another engine, or no table): the caller handles it inline, as before.</summary>
  Unsupported = 0,

  /// <summary>This call recorded the delivery.</summary>
  Recorded = 1,

  /// <summary>The delivery was already recorded (another copy of the same message, or a retry); nothing changed.</summary>
  AlreadyRecorded = 2,
}
