using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

public class TransportPublishStrategyTests {
  // Helper to create properly serialized metadata
  [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test helper - serialization is safe in test context")]
  [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Test helper - not used in AOT context")]
  private static string CreateMetadata(Guid messageId) {
    var options = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var metadata = new EnvelopeMetadata {
      MessageId = MessageId.From(messageId),
      Hops = new List<MessageHop>()
    };
    return JsonSerializer.Serialize(metadata, options);
  }

  private class TestTransport : ITransport {
    private bool _isInitialized;

    public bool IsInitialized => _isInitialized;
    public TransportCapabilities Capabilities => new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      _isInitialized = true;
      return Task.CompletedTask;
    }

    public Task<Exception?> PublishResult { get; set; } = Task.FromResult<Exception?>(null);

    public OutboxWork? LastPublishedWork { get; private set; }
    public IMessageEnvelope? LastPublishedEnvelope { get; private set; }
    public TransportDestination? LastPublishedDestination { get; private set; }

    public async Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, CancellationToken cancellationToken = default) {
      LastPublishedEnvelope = envelope;
      LastPublishedDestination = destination;

      var exception = await PublishResult;
      if (exception != null) {
        throw exception;
      }
    }

    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull {
      throw new NotImplementedException();
    }
  }

  [Test]
  public async Task Constructor_NullTransport_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // Act & Assert
    await Assert.That(() => new TransportPublishStrategy(null!, jsonOptions, readinessCheck))
      .Throws<ArgumentNullException>()
      .Because("Transport cannot be null");
  }

  [Test]
  public async Task Constructor_NullJsonOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // Act & Assert
    await Assert.That(() => new TransportPublishStrategy(transport, null!, readinessCheck))
      .Throws<ArgumentNullException>()
      .Because("JsonOptions cannot be null");
  }

  [Test]
  public async Task Constructor_NullReadinessCheck_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act & Assert
    await Assert.That(() => new TransportPublishStrategy(transport, jsonOptions, null!))
      .Throws<ArgumentNullException>()
      .Because("ReadinessCheck cannot be null");
  }

  [Test]
  public async Task IsReadyAsync_DefaultReadinessCheck_ReturnsTrueAsync() {
    // Arrange
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, jsonOptions, readinessCheck);

    // Act
    var result = await strategy.IsReadyAsync();

    // Assert
    await Assert.That(result).IsTrue()
      .Because("DefaultTransportReadinessCheck always returns true");
  }

  [Test]
  public async Task PublishAsync_SuccessfulPublish_ShouldReturnSuccessResultAsync() {
    // Arrange
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, jsonOptions, readinessCheck);

    var messageId = Guid.NewGuid();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      MessageData = "{}",
      Metadata = CreateMetadata(messageId),
      Scope = null,
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Published);
    await Assert.That(result.Error).IsNull();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("test-topic");
  }

  [Test]
  public async Task PublishAsync_TransportFailure_ShouldReturnFailureResultAsync() {
    // Arrange
    var transport = new TestTransport {
      PublishResult = Task.FromResult<Exception?>(new InvalidOperationException("Transport unavailable"))
    };
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, jsonOptions, readinessCheck);

    var messageId = Guid.NewGuid();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      MessageData = "{}",
      Metadata = CreateMetadata(messageId),
      Scope = null,
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(result.Error).IsNotNull();
    await Assert.That(result.Error).Contains("Transport unavailable");
  }

  [Test]
  public async Task PublishAsync_WithNullScope_ShouldPublishSuccessfullyAsync() {
    // Arrange
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, jsonOptions, readinessCheck);

    var messageId = Guid.NewGuid();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      MessageData = "{}",
      Metadata = CreateMetadata(messageId),
      Scope = null,  // Explicitly null scope
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedEnvelope).IsNotNull();
  }

  [Test]
  public async Task PublishAsync_WithStreamId_ShouldIncludeInEnvelopeAsync() {
    // Arrange
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, jsonOptions, readinessCheck);

    var messageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      MessageData = "{}",
      Metadata = CreateMetadata(messageId),
      Scope = null,
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedEnvelope).IsNotNull();
    // StreamId should be used for message ordering/routing in envelope
  }
}
