// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// The ONE code path for declaring RabbitMQ subscription infrastructure (topology arc
/// phase 5): both the transport's subscribe-time setup and the provisioner's manifest-driven
/// DARK provisioning delegate here, so exchange/queue arguments and binding shapes can never
/// drift between the two (a queue re-declared with different arguments fails with
/// PRECONDITION_FAILED).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerManifestTests.cs:ProvisionManifest_QueueArguments_MatchTransportConventionAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
internal static class RabbitMQEntityProvisioning {
  /// <summary>
  /// Declares the full subscription infrastructure on <paramref name="channel"/>: the topic
  /// exchange, the dead-letter exchange + queue (when enabled), the subscription queue with
  /// the transport's argument set, and one binding per routing pattern; explicit patterns also
  /// remove a legacy match-all <c>#</c> binding (#2089). Every declare is broker-idempotent when
  /// the arguments match.
  /// <para>
  /// A queue an earlier version declared with other arguments (#2421: it dead-lettered into a fanout with no routing
  /// key) is refused by the broker with PRECONDITION_FAILED, which also closes <paramref name="channel"/>. Such a queue
  /// is deleted and re-declared when it is empty and has no consumers, and the work continues on a channel from
  /// <paramref name="openChannelAsync"/>; one that holds messages or is still consumed is refused by name, so a deploy
  /// never drops a message.
  /// </para>
  /// </summary>
  /// <returns>The channel the caller continues on: <paramref name="channel"/> itself, or, when the queue was
  /// re-declared, a channel from <paramref name="openChannelAsync"/> that the caller now owns.</returns>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Integration.Tests/RabbitMQDeadLetterIsolationIntegrationTests.cs:PoisonFromOneSubscriber_LandsOnlyInItsOwnDeadLetterQueueAndStoreAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Integration.Tests/RabbitMQDeadLetterUpgradeIntegrationTests.cs</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQDeadLetterUpgradeTests.cs</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Declares the exchange, the dead-letter pair, the queue and one binding per routing pattern in a single broker-idempotent call. The parameters are the topology being declared, and splitting the call is what the single call exists to avoid.")]
  internal static async Task<IChannel> DeclareSubscriptionInfrastructureAsync(
    IChannel channel,
    Func<CancellationToken, Task<IChannel>> openChannelAsync,
    RabbitMQOptions options,
    string exchangeName,
    string queueName,
    IReadOnlyList<string> routingPatterns,
    ILogger? logger,
    CancellationToken cancellationToken,
    bool controlClass = false
  ) {
    await channel.ExchangeDeclareAsync(
      exchange: exchangeName,
      type: "topic",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    if (options.AutoDeclareDeadLetterExchange) {
      await DeclareDeadLetterExchangeAsync(channel, exchangeName, queueName, cancellationToken);
    }

    var queueArgs = new Dictionary<string, object?>();
    if (options.AutoDeclareDeadLetterExchange) {
      // #2421: the queue dead-letters through the exchange's DIRECT dead-letter exchange with its own DLQ name as the
      // routing key, and the DLQ is bound only on that key, so a poison message reaches this queue's DLQ and no other
      // subscriber's.
      queueArgs["x-dead-letter-exchange"] = DeadLetterExchangeName(exchangeName);
      queueArgs["x-dead-letter-routing-key"] = DeadLetterQueueName(queueName);
    }
    // Control class (topology arc phase 9): the plain-queue analogue of a sessionless Service Bus
    // subscription. RabbitMQ's ordering primitive pins a queue to ONE consumer at a time; control
    // consumers need no ordering, so pinning them only reintroduces the head-of-line queueing the
    // class exists to avoid. The dead-letter exchange above is untouched — plain is about ordering,
    // never about giving up the broker's own poison valve.
    if (options.EnableSingleActiveConsumer && !controlClass) {
      queueArgs["x-single-active-consumer"] = true;
    }

    try {
      await _declareQueueAsync(channel, queueName, queueArgs, cancellationToken);
    } catch (OperationInterruptedException ex) when (_isReply(ex, Constants.PreconditionFailed)) {
      channel = await _redeclareEarlierVersionQueueAsync(
        openChannelAsync, options, exchangeName, queueName, queueArgs, logger, cancellationToken);
    }

    foreach (var pattern in routingPatterns) {
      if (logger?.IsEnabled(LogLevel.Debug) == true) {
        logger.LogDebug(
          "Binding queue {QueueName} to exchange {ExchangeName} with routing pattern {Pattern}",
          queueName,
          exchangeName,
          pattern
        );
      }

      await channel.QueueBindAsync(
        queue: queueName,
        exchange: exchangeName,
        routingKey: pattern,
        arguments: null,
        noWait: false,
        cancellationToken: cancellationToken
      );
    }

    // #2089: explicit patterns replace a match-all binding. A queue declared before its patterns (a command inbox bound
    // with '#' before it filtered to the command types it handles) keeps receiving everything while the '#' binding
    // stays beside the new ones, so it is removed. The broker treats unbinding a binding that does not exist as a no-op.
    if (!routingPatterns.Contains(MATCH_ALL_PATTERN)) {
      await channel.QueueUnbindAsync(
        queue: queueName,
        exchange: exchangeName,
        routingKey: MATCH_ALL_PATTERN,
        arguments: null,
        cancellationToken: cancellationToken
      );
    }

    return channel;
  }

  private static Task<QueueDeclareOk> _declareQueueAsync(
      IChannel channel, string queueName, Dictionary<string, object?> queueArgs, CancellationToken cancellationToken) =>
    channel.QueueDeclareAsync(
      queue: queueName,
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: queueArgs,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

  /// <summary>
  /// The upgrade of a queue the broker refused to re-declare with this version's arguments (#2421). RabbitMQ cannot
  /// change a queue's arguments, so the queue is deleted and declared again; that is safe only when it holds no
  /// message and no consumer holds an unacknowledged one, so anything else is refused by name. The delete itself
  /// repeats both conditions, so a message or consumer that arrives after the check refuses it too. Then the DLQ
  /// leaves the earlier fanout dead-letter exchange, which is deleted once no DLQ is bound to it.
  /// </summary>
  private static async Task<IChannel> _redeclareEarlierVersionQueueAsync(
      Func<CancellationToken, Task<IChannel>> openChannelAsync,
      RabbitMQOptions options,
      string exchangeName,
      string queueName,
      Dictionary<string, object?> queueArgs,
      ILogger? logger,
      CancellationToken cancellationToken) {
    await _deleteIfEmptyAndUnconsumedAsync(openChannelAsync, queueName, cancellationToken);

    var channel = await openChannelAsync(cancellationToken);
    try {
      await _declareQueueAsync(channel, queueName, queueArgs, cancellationToken);
      if (logger is not null) {
        _logQueueDeclaredAgain(logger, queueName, null);
      }
      if (options.AutoDeclareDeadLetterExchange) {
        await _retireEarlierFanoutDeadLetterExchangeAsync(
          openChannelAsync, exchangeName, DeadLetterQueueName(queueName), logger, cancellationToken);
      }
    } catch {
      channel.Dispose();
      throw;
    }
    return channel;
  }

  private static async Task _deleteIfEmptyAndUnconsumedAsync(
      Func<CancellationToken, Task<IChannel>> openChannelAsync, string queueName, CancellationToken cancellationToken) {
    var probe = await openChannelAsync(cancellationToken);
    await using (probe) {
      var state = await probe.QueueDeclarePassiveAsync(queueName, cancellationToken);
      if (state.MessageCount > 0 || state.ConsumerCount > 0) {
        throw _upgradeRefused(
          queueName, $"it holds {state.MessageCount} ready message(s) and has {state.ConsumerCount} consumer(s)", inner: null);
      }
      try {
        await probe.QueueDeleteAsync(queueName, ifUnused: true, ifEmpty: true, cancellationToken: cancellationToken);
      } catch (OperationInterruptedException ex) when (_isReply(ex, Constants.PreconditionFailed)) {
        throw _upgradeRefused(queueName, "a message or a consumer reached it while it was being upgraded", ex);
      }
    }
  }

  private static readonly Action<ILogger, string, Exception?> _logQueueDeclaredAgain =
    LoggerMessage.Define<string>(
      LogLevel.Warning,
      new EventId(0, "RabbitMQQueueDeclaredAgain"),
      "RabbitMQ queue {QueueName} was declared by an earlier version with other arguments; it was empty and had no consumers, so it was deleted and declared again");

  private static readonly Action<ILogger, string, Exception?> _logEarlierFanoutDeleted =
    LoggerMessage.Define<string>(
      LogLevel.Information,
      new EventId(0, "RabbitMQEarlierFanoutDeadLetterExchangeDeleted"),
      "Deleted the earlier fanout dead-letter exchange {ExchangeName}: no dead-letter queue is bound to it any more");

  private static readonly Action<ILogger, string, Exception?> _logEarlierFanoutKept =
    LoggerMessage.Define<string>(
      LogLevel.Information,
      new EventId(0, "RabbitMQEarlierFanoutDeadLetterExchangeKept"),
      "Kept the earlier fanout dead-letter exchange {ExchangeName}: it is absent or another dead-letter queue is still bound to it");

  /// <summary>True when the broker closed the channel with <paramref name="replyCode"/>.</summary>
  private static bool _isReply(OperationInterruptedException ex, int replyCode) =>
    ex.ShutdownReason?.ReplyCode == replyCode;

  private static InvalidOperationException _upgradeRefused(string queueName, string state, Exception? inner) =>
    new(
      $"RabbitMQ queue '{queueName}' was declared by an earlier version with other arguments (the dead-letter route " +
      "changed in #2421), and RabbitMQ cannot change a queue's arguments in place. Such a queue is deleted and " +
      $"declared again only when it is empty and has no consumers, so that no message is lost, but {state}. To " +
      "upgrade it, stop every instance that still consumes from it, move or drain its messages (for example with a " +
      "shovel to another queue), delete the queue, then start this version, which declares it again.",
      inner);

  /// <summary>
  /// Moves <paramref name="dlqName"/> off the fanout dead-letter exchange earlier versions declared, then deletes that
  /// exchange if no DLQ is bound to it any more. A service still on an earlier version keeps its DLQ bound there, and
  /// its queue still dead-letters into it, so the fanout stays until the last such service upgrades.
  /// </summary>
  private static async Task _retireEarlierFanoutDeadLetterExchangeAsync(
      Func<CancellationToken, Task<IChannel>> openChannelAsync,
      string exchangeName,
      string dlqName,
      ILogger? logger,
      CancellationToken cancellationToken) {
    var earlierExchange = EarlierFanoutDeadLetterExchangeName(exchangeName);
    var channel = await openChannelAsync(cancellationToken);
    await using (channel) {
      try {
        await channel.QueueUnbindAsync(dlqName, earlierExchange, routingKey: "", arguments: null, cancellationToken);
        await channel.ExchangeDeleteAsync(earlierExchange, ifUnused: true, cancellationToken: cancellationToken);
        if (logger is not null) {
          _logEarlierFanoutDeleted(logger, earlierExchange, null);
        }
      } catch (OperationInterruptedException ex) when (_isReply(ex, Constants.NotFound) || _isReply(ex, Constants.PreconditionFailed)) {
        // NOT_FOUND: this broker has no earlier fanout. PRECONDITION_FAILED: another service's DLQ is still bound to it,
        // because that service is still on an earlier version; the last one to upgrade deletes it.
        if (logger is not null) {
          _logEarlierFanoutKept(logger, earlierExchange, ex);
        }
      }
    }
  }

  /// <summary>The topic-exchange pattern that matches every routing key.</summary>
  private const string MATCH_ALL_PATTERN = "#";

  /// <summary>
  /// The dead-letter exchange every subscription queue on <paramref name="exchangeName"/> dead-letters through: one
  /// DIRECT exchange per exchange (#2421). The name differs from the fanout <c>{exchange}.dlx</c> earlier versions
  /// declared, because RabbitMQ cannot change an exchange's type in place and services still on an earlier version
  /// keep dead-lettering into the fanout until they are upgraded.
  /// </summary>
  internal static string DeadLetterExchangeName(string exchangeName) => $"{exchangeName}.dlx.direct";

  /// <summary>The fanout dead-letter exchange versions before #2421 declared for <paramref name="exchangeName"/>.</summary>
  internal static string EarlierFanoutDeadLetterExchangeName(string exchangeName) => $"{exchangeName}.dlx";

  /// <summary>The dead-letter queue of <paramref name="queueName"/>, and its routing key on the dead-letter exchange.</summary>
  internal static string DeadLetterQueueName(string queueName) => $"{queueName}.dlq";

  /// <summary>
  /// Declares the direct dead-letter exchange of <paramref name="exchangeName"/> and the dead-letter queue of
  /// <paramref name="queueName"/>, bound to it only on the DLQ's own name: the routing key the queue dead-letters with.
  /// </summary>
  internal static async Task DeclareDeadLetterExchangeAsync(
    IChannel channel,
    string exchangeName,
    string queueName,
    CancellationToken cancellationToken
  ) {
    var dlxName = DeadLetterExchangeName(exchangeName);
    var dlqName = DeadLetterQueueName(queueName);

    await channel.ExchangeDeclareAsync(
      exchange: dlxName,
      type: "direct",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    await channel.QueueDeclareAsync(
      queue: dlqName,
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    await channel.QueueBindAsync(
      queue: dlqName,
      exchange: dlxName,
      routingKey: dlqName,
      arguments: null,
      noWait: false,
      cancellationToken: cancellationToken
    );
  }
}
