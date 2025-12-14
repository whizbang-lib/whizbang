using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:Constructor_NullTransport_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:Constructor_NullReadinessCheck_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:IsReadyAsync_DefaultReadinessCheck_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_SuccessfulPublish_ShouldReturnSuccessResultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_TransportFailure_ShouldReturnFailureResultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithNullScope_ShouldPublishSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithStreamId_ShouldIncludeInEnvelopeAsync</tests>
/// Default implementation of IMessagePublishStrategy that publishes messages via ITransport.
/// Publishes envelope objects directly to the configured transport.
/// </summary>
public class TransportPublishStrategy : IMessagePublishStrategy {
  private readonly ITransport _transport;
  private readonly ITransportReadinessCheck _readinessCheck;

  /// <summary>
  /// Creates a new TransportPublishStrategy.
  /// </summary>
  /// <param name="transport">The transport to publish messages to</param>
  /// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
  public TransportPublishStrategy(ITransport transport, ITransportReadinessCheck readinessCheck) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
  }

  /// <summary>
  /// Checks if the transport is ready to accept messages by delegating to the readiness check.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if transport is ready, false otherwise</returns>
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return _readinessCheck.IsReadyAsync(cancellationToken);
  }

  /// <summary>
  /// Publishes a single outbox message to the configured transport.
  /// Envelope is already deserialized - publishes directly via ITransport.
  /// </summary>
  /// <param name="work">The outbox work item containing the message to publish</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Result indicating success/failure and any error details</returns>
  public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
    try {
      // Create transport destination
      var destination = new TransportDestination(work.Destination);

      // Publish to transport - envelope is already deserialized
      // OutboxWork is non-generic, Envelope is IMessageEnvelope<object>
      await _transport.PublishAsync(work.Envelope, destination, cancellationToken);

      // Return success result
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    } catch (Exception ex) {
      // Return failure result with error details
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = work.Status, // Already stored, publish failed
        Error = $"{ex.GetType().Name}: {ex.Message}"
      };
    }
  }
}

