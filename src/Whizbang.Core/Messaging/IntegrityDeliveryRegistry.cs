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

  /// <summary>Initializes a new instance, with the redelivery reconcile handler registered.</summary>
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
}
