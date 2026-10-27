// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration options for message processing concurrency.
/// Controls how the transport consumer workers manage DB connection pressure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency control:</b> Each concurrent message handler holds a DB connection
/// from the pool for the duration of its inbox write (<c>store_inbox_messages</c>). With multiple subscriptions
/// and high <c>MaxConcurrentSessions</c>, the total concurrent handlers can exceed the
/// connection pool size. <see cref="MaxConcurrentMessages"/> caps total concurrent handlers
/// across all subscriptions via a shared semaphore.
/// </para>
/// <para>
/// <b>Inbox batching:</b> Instead of each handler making its own <c>store_inbox_messages</c>
/// DB call, the <see cref="IInboxBatchStrategy"/> collects messages and flushes them in a
/// single call. When the flush triggers is configured on <see cref="SlidingWindowInboxOptions"/>
/// (<c>Whizbang:Workers:InboxBatch</c>), not here.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Override defaults before AddTransportConsumer():
/// services.AddSingleton(new MessageProcessingOptions {
///     MaxConcurrentMessages = 80
/// });
/// </code>
/// </example>
/// <docs>messaging/transports/transport-consumer#message-processing-options</docs>
public sealed class MessageProcessingOptions {
  /// <summary>
  /// Maximum number of messages processed concurrently across ALL subscriptions.
  /// Each concurrent message holds a DB connection from the pool for the duration
  /// of inbox dedup, lifecycle stages, and completion flush.
  /// <para>
  /// Set to 0 to disable the global concurrency limit (not recommended).
  /// </para>
  /// Default: 40 (leaves headroom in a 100-connection pool for health checks,
  /// outbox publishing, perspectives, and other DB operations).
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#concurrency</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessageProcessingOptionsTests.cs:DefaultValues_MatchDocumentedDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessageProcessingOptionsTests.cs:MaxConcurrentMessages_ZeroDisablesSemaphoreAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/ServiceBusConsumerWorkerGapTests.cs:HandleMessage_ConcurrencyLimitDisabled_ProcessesWithoutSemaphoreAsync</tests>
  public int MaxConcurrentMessages { get; set; } = 40;
}
