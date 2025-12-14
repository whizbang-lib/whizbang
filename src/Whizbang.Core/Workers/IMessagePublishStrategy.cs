using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

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
