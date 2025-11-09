using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for ITransport interface contract.
/// These tests define the expected behavior of any transport implementation.
/// Following TDD: These tests are written BEFORE the interface implementation.
/// All tests should FAIL initially (RED phase), then pass after implementation (GREEN phase).
/// </summary>
public class ITransportTests {
  [Test]
  public async Task ITransport_Capabilities_ReturnsTransportCapabilitiesAsync() {
    // Arrange
    var transport = CreateTestTransport();

    // Act
    var capabilities = transport.Capabilities;

    // Assert - Capabilities is an enum (value type), check it's not None
    await Assert.That(capabilities).IsNotEqualTo(TransportCapabilities.None);
  }

  [Test]
  public async Task ITransport_PublishAsync_WithValidMessage_CompletesSuccessfullyAsync() {
    // Arrange
    var transport = CreateTestTransport();
    var envelope = CreateTestEnvelope();
    var destination = new TransportDestination("test-topic");

    // Act & Assert - Should not throw
    await transport.PublishAsync(envelope, destination, CancellationToken.None);
  }

  [Test]
  public async Task ITransport_PublishAsync_WithCancellation_ThrowsOperationCanceledAsync() {
    // Arrange
    var transport = CreateTestTransport();
    var envelope = CreateTestEnvelope();
    var destination = new TransportDestination("test-topic");
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await transport.PublishAsync(envelope, destination, cts.Token)
    );
  }

  [Test]
  public async Task ITransport_SubscribeAsync_RegistersHandler_ReturnsSubscriptionAsync() {
    // Arrange
    var transport = CreateTestTransport();
    var destination = new TransportDestination("test-topic");
    var handlerCalled = false;
    Func<IMessageEnvelope, CancellationToken, Task> handler = (env, ct) => {
      handlerCalled = true;
      return Task.CompletedTask;
    };

    // Act
    var subscription = await transport.SubscribeAsync(handler, destination, CancellationToken.None);

    // Assert
    await Assert.That(subscription).IsNotNull();
  }

  [Test]
  [Skip("Comprehensive request-response test exists in InProcessTransportTests")]
  public async Task ITransport_SendAsync_WithRequestResponse_ReturnsResponseEnvelopeAsync() {
    // Arrange
    var transport = CreateTestTransport();
    var requestEnvelope = CreateTestEnvelope();
    var destination = new TransportDestination("test-service");

    // Setup responder
    await transport.SubscribeAsync(
      handler: async (env, ct) => {
        // Simulate responder sending response
        var responseEnvelope = new MessageEnvelope<TestResponse> {
          MessageId = MessageId.New(),
          Payload = new TestResponse { Result = "response" },
          Hops = new List<MessageHop> {
            new MessageHop { ServiceName = "Test", Timestamp = DateTimeOffset.UtcNow }
          }
        };
        var responseDestination = new TransportDestination($"response-{env.MessageId.Value}");
        await transport.PublishAsync(responseEnvelope, responseDestination, ct);
      },
      destination: destination
    );

    // Act
    var responseEnvelope = await transport.SendAsync<TestMessage, TestResponse>(
      requestEnvelope,
      destination,
      CancellationToken.None
    );

    // Assert
    await Assert.That(responseEnvelope).IsNotNull();
    // Note: IMessageEnvelope doesn't expose Payload, need to cast to get typed payload
    // This test will be enhanced in InProcessTransport tests
  }

  [Test]
  public async Task ITransport_SendAsync_WithTimeout_ThrowsTimeoutExceptionAsync() {
    // Arrange
    var transport = CreateTestTransport();
    var requestEnvelope = CreateTestEnvelope();
    var destination = new TransportDestination("test-service");
    var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await transport.SendAsync<TestMessage, TestResponse>(
        requestEnvelope,
        destination,
        cts.Token
      )
    );
  }

  // Helper methods
  private ITransport CreateTestTransport() {
    // This will use InProcessTransport once implemented
    // For now, this will fail compilation - that's expected in RED phase
    return new InProcessTransport();
  }

  private IMessageEnvelope CreateTestEnvelope() {
    var message = new TestMessage { Content = "Test" };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = new List<MessageHop> {
        new MessageHop { ServiceName = "Test", Timestamp = DateTimeOffset.UtcNow }
      }
    };
  }

  // Test message types
  private record TestMessage {
    public string Content { get; init; } = string.Empty;
  }

  private record TestResponse {
    public string Result { get; init; } = string.Empty;
  }
}
