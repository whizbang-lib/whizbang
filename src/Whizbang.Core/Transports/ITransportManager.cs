using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Manages multiple transport instances and handles publishing/subscribing across them.
/// Enables policy-based routing where messages can be published to multiple transports
/// and subscriptions can be created from multiple sources.
/// </summary>
/// <docs>components/transports</docs>
public interface ITransportManager {
  /// <summary>
  /// Registers a transport for a specific transport type.
  /// If a transport already exists for this type, it will be replaced.
  /// </summary>
  /// <param name="type">The transport type</param>
  /// <param name="transport">The transport instance</param>
  void AddTransport(TransportType type, ITransport transport);

  /// <summary>
  /// Gets a registered transport by type.
  /// </summary>
  /// <param name="type">The transport type</param>
  /// <returns>The transport instance</returns>
  /// <exception cref="InvalidOperationException">If no transport is registered for the type</exception>
  ITransport GetTransport(TransportType type);

  /// <summary>
  /// Checks if a transport is registered for the specified type.
  /// </summary>
  /// <param name="type">The transport type</param>
  /// <returns>True if a transport is registered, false otherwise</returns>
  bool HasTransport(TransportType type);

  /// <summary>
  /// Publishes a message to multiple transport targets (fan-out pattern).
  /// Each target specifies which transport to use and the destination.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to publish</param>
  /// <param name="targets">The list of publish targets</param>
  /// <param name="context">Optional message context</param>
  /// <exception cref="InvalidOperationException">If a transport is not registered for any target</exception>
  Task PublishToTargetsAsync<TMessage>(
    TMessage message,
    IReadOnlyList<PublishTarget> targets,
    IMessageContext? context = null
  );

  /// <summary>
  /// Creates subscriptions from multiple transport sources.
  /// Each subscription listens to a different source and invokes the handler for incoming messages.
  /// </summary>
  /// <param name="targets">The list of subscription targets</param>
  /// <param name="handler">The handler to invoke for each incoming message</param>
  /// <returns>List of active subscriptions</returns>
  /// <exception cref="InvalidOperationException">If a transport is not registered for any target</exception>
  Task<List<ISubscription>> SubscribeFromTargetsAsync(
    IReadOnlyList<SubscriptionTarget> targets,
    Func<IMessageEnvelope, Task> handler
  );
}
