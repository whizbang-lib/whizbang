// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Transports;

/// <summary>
/// Interface for checking whether a transport is ready to accept messages.
/// Implementations can check connectivity, health, or other readiness criteria.
/// </summary>
/// <remarks>
/// <para>This interface is consulted (through <c>TransportPublishStrategy.IsReadyAsync</c>) by the default
/// publisher, <c>OutboxDrainWorker</c>, before each publish pass, and by the <c>OutboxPublishWorker</c> rollback path,
/// to determine if messages should be published or held. While a transport is not ready, the drain worker publishes
/// nothing, renews the leases of the rows it holds and charges no attempt; the rows publish once the transport
/// reports ready (#1558).</para>
///
/// <para>Examples of readiness checks:
/// - Azure Service Bus: Check if namespace is accessible
/// - RabbitMQ: Check if connection is established
/// - Kafka: Check if brokers are reachable
/// - HTTP: Check if endpoint is responding</para>
/// </remarks>
/// <docs>messaging/transports/transports</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:AlwaysReadyCheck_ReturnsTrue_WhenCalledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:ConfigurableCheck_ReturnsConfiguredValue_WhenCalledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:ConfigurableCheck_RespectsCancellationToken_WhenCanceledAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/OutboxDrainWorkerTransportReadinessTests.cs:NotReady_HoldsTheRowsWithLeasesRenewed_ThenPublishesThemOnceReadyAsync</tests>
public interface ITransportReadinessCheck {
  /// <summary>
  /// Checks if the transport is ready to accept messages.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token to cancel the readiness check.</param>
  /// <returns>True if the transport is ready, false otherwise.</returns>
  /// <remarks>
  /// This method should be fast and lightweight. If the check requires network I/O,
  /// consider implementing caching or circuit breaker patterns to avoid excessive overhead.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:AlwaysReadyCheck_ReturnsTrue_WhenCalledAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:NeverReadyCheck_ReturnsFalse_WhenCalledAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:ConfigurableCheck_ReturnsConfiguredValue_WhenCalledAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Transports/TransportReadinessCheckTests.cs:ConfigurableCheck_RespectsCancellationToken_WhenCanceledAsync</tests>
  Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
