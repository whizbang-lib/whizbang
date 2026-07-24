using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Strategy interface for publishing outbox messages to a transport.
/// Implementations define how messages are published (e.g., to Azure Service Bus, AWS SQS, etc.).
/// This abstraction allows pluggable publish logic in WorkCoordinatorPublisherWorker.
/// </summary>
public interface IMessagePublishStrategy {
  /// <summary>
  /// Checks if the transport is ready to accept messages.
  /// Implementations should check transport connectivity and return false if not ready.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:IMessagePublishStrategy_DefaultIsReadyAsync_ShouldReturnTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:IMessagePublishStrategy_CustomIsReadyAsync_CanBeOverriddenAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:IMessagePublishStrategy_IsReadyAsync_RespectsCancellationTokenAsync</tests>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if the transport is ready to accept messages, false otherwise</returns>
  Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Publishes a single outbox message to the configured transport.
  /// Called by WorkCoordinatorPublisherWorker's internal publisher loop.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:IMessagePublishStrategy_Interface_ShouldHavePublishAsyncMethodAsync</tests>
  /// <param name="work">The outbox work item containing the message to publish</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Result indicating success/failure and any error details</returns>
  Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken);

  /// <summary>
  /// Whether this strategy supports bulk publishing.
  /// When true, PublishBatchAsync can be called to publish multiple messages in a single transport operation.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:SupportsBulkPublish_WithBulkCapableTransport_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:SupportsBulkPublish_WithoutBulkCapableTransport_ReturnsFalseAsync</tests>
  bool SupportsBulkPublish => false;

  /// <summary>
  /// Publishes a batch of outbox messages to the configured transport.
  /// Groups messages by resolved destination and uses bulk transport when available.
  /// Returns per-message results to enable partial failure handling.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_SingleDestination_CallsTransportOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_MultipleDestinations_GroupsByAddressAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_EventStoreOnlyItems_ReturnSuccessWithoutCallingTransportAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_TransportThrowsForGroup_FailsOnlyThatGroupAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_PerItemRoutingKeys_SetCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_EmptyList_ReturnsEmptyResultsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_PartialItemResults_MapsCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_AllEventStoreOnly_NoTransportCallsAsync</tests>
  /// <param name="workItems">The batch of outbox work items to publish</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Per-message results indicating success or failure</returns>
  Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(
    IReadOnlyList<OutboxWork> workItems,
    CancellationToken cancellationToken
  ) => throw new NotSupportedException("Bulk publish is not supported by this strategy.");
}

/// <summary>
/// Result of a message publish operation.
/// Contains the message ID, success status, completed processing stages, and any error details.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Failure_ShouldHaveErrorMessageAsync</tests>
public record MessagePublishResult {
  /// <summary>
  /// The message ID that was published (or attempted to publish).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Failure_ShouldHaveErrorMessageAsync</tests>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Whether the publish operation succeeded.
  /// True = message was successfully published to transport.
  /// False = publish failed (Error will contain details).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Failure_ShouldHaveErrorMessageAsync</tests>
  public required bool Success { get; init; }

  /// <summary>
  /// The processing status flags that were completed.
  /// For successful publish: MessageProcessingStatus.Published
  /// For partial completion: MessageProcessingStatus.Stored (already stored, publish failed)
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Failure_ShouldHaveErrorMessageAsync</tests>
  public required MessageProcessingStatus CompletedStatus { get; init; }

  /// <summary>
  /// Error message if Success is false. Null if successful.
  /// Contains exception details or transport-specific error information.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Failure_ShouldHaveErrorMessageAsync</tests>
  public string? Error { get; init; }

  /// <summary>
  /// Classified reason for the failure.
  /// Enables typed filtering and handling of different failure scenarios.
  /// Defaults to Unknown for failures, None for successes.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MessagePublishStrategyTests.cs:MessagePublishResult_Failure_ShouldHaveErrorMessageAsync</tests>
  public MessageFailureReason Reason { get; init; } = MessageFailureReason.Unknown;
}
