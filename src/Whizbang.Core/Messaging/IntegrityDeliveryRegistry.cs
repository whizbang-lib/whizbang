// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>Runs one integrity delivery inside the maintainer's drain.</summary>
/// <param name="payload">The delivery as recorded.</param>
/// <param name="services">A scope's services for this delivery.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>A task that completes when the delivery is handled; a fault counts a failed attempt.</returns>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
public delegate Task IntegrityDeliveryHandler(string payload, IServiceProvider services, CancellationToken cancellationToken);

/// <summary>
/// Runs several integrity deliveries of one type together inside the maintainer's drain (#2099), when doing them
/// together costs less than doing them one at a time.
/// </summary>
/// <param name="payloads">The deliveries as recorded, in arrival order.</param>
/// <param name="services">A scope's services for the batch.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>A task that completes when every delivery is handled. A fault fails none of them: the drain handles each
/// delivery on its own instead, so one bad delivery fails alone.</returns>
/// <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
public delegate Task IntegrityDeliveryBatchHandler(
  IReadOnlyList<string> payloads, IServiceProvider services, CancellationToken cancellationToken);

/// <summary>
/// Maps each integrity delivery type to the handler the maintainer's drain runs it with (#2001). Populated by the
/// registrations that defer integrity receptors (<see cref="IntegrityDeliveryRegistration"/>); Core's own deferred
/// work, the redelivery bundle reconcile, is registered by the registry itself, so no registrar has to remember it.
/// </summary>
/// <remarks>
/// Registered by the worker pipeline beside the drain. Its presence is what tells a deferring receptor that something
/// will drain what it records; on a host without it, integrity receptors run inline, as before.
/// </remarks>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs</tests>
public sealed class IntegrityDeliveryRegistry {
  private readonly ConcurrentDictionary<string, IntegrityDeliveryHandler> _handlers = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, IntegrityDeliveryBatchHandler> _batchHandlers = new(StringComparer.Ordinal);

  /// <summary>Initializes a new instance, with the redelivery reconcile handler and its batch handler registered.</summary>
  public IntegrityDeliveryRegistry() {
    Register(RedeliveryReconciler.DELIVERY_TYPE, RedeliveryReconciler.HandleDeferredAsync);
  }

  /// <summary>
  /// Registers the handler for <paramref name="deliveryType"/>. The first registration of a type wins, so a registrar
  /// that starts twice changes nothing and a host that registers its own handler first keeps it.
  /// </summary>
  /// <param name="deliveryType">The delivery type.</param>
  /// <param name="handler">The handler.</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:TheFirstRegistrationOfAType_WinsAsync</tests>
  public void Register(string deliveryType, IntegrityDeliveryHandler handler) {
    ArgumentNullException.ThrowIfNull(deliveryType);
    ArgumentNullException.ThrowIfNull(handler);
    _handlers.TryAdd(deliveryType, handler);
  }

  /// <summary>Finds the handler for <paramref name="deliveryType"/>.</summary>
  /// <param name="deliveryType">The delivery type.</param>
  /// <param name="handler">The handler, when one is registered.</param>
  /// <returns>Whether a handler is registered.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:AnUnknownType_HasNoHandlerAsync</tests>
  public bool TryGetHandler(string deliveryType, out IntegrityDeliveryHandler? handler) =>
    _handlers.TryGetValue(deliveryType, out handler);

  /// <summary>
  /// Registers a batch handler for <paramref name="deliveryType"/> (#2099): the drain hands it every delivery of that
  /// type it leased together, instead of one at a time. The type keeps its single handler, which the drain falls back
  /// to when the batch handler faults. The first registration of a type wins.
  /// </summary>
  /// <param name="deliveryType">The delivery type.</param>
  /// <param name="handler">The batch handler.</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:TheRedeliveryReconcile_HasABatchHandler_AndTheFirstBatchRegistrationWinsAsync</tests>
  public void RegisterBatch(string deliveryType, IntegrityDeliveryBatchHandler handler) {
    ArgumentNullException.ThrowIfNull(deliveryType);
    ArgumentNullException.ThrowIfNull(handler);
    _batchHandlers.TryAdd(deliveryType, handler);
  }

  /// <summary>Finds the batch handler for <paramref name="deliveryType"/>.</summary>
  /// <param name="deliveryType">The delivery type.</param>
  /// <param name="handler">The batch handler, when one is registered.</param>
  /// <returns>Whether a batch handler is registered.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:TheRedeliveryReconcile_HasABatchHandler_AndTheFirstBatchRegistrationWinsAsync</tests>
  public bool TryGetBatchHandler(string deliveryType, out IntegrityDeliveryBatchHandler? handler) =>
    _batchHandlers.TryGetValue(deliveryType, out handler);
}
