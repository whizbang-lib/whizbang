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
    var transport = _createTestTransport();

    // Act
    var capabilities = transport.Capabilities;

    // Assert - Capabilities is an enum (value type), check it's not None
    await Assert.That(capabilities).IsNotEqualTo(TransportCapabilities.None);
  }

  [Test]
  public async Task ITransport_PublishAsync_WithValidMessage_CompletesSuccessfullyAsync() {
    // Arrange
    var transport = _createTestTransport();
    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-topic");

    // Act & Assert - Should not throw
    await transport.PublishAsync(envelope, destination, envelopeType: null, CancellationToken.None);
  }

  [Test]
  public async Task ITransport_PublishAsync_WithCancellation_ThrowsOperationCanceledAsync() {
    // Arrange
    var transport = _createTestTransport();
    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-topic");
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await transport.PublishAsync(envelope, destination, envelopeType: null, cts.Token)
    );
  }

  [Test]
  public async Task ITransport_SubscribeAsync_RegistersHandler_ReturnsSubscriptionAsync() {
    // Arrange
    var transport = _createTestTransport();
    var destination = new TransportDestination("test-topic");
    Task handler(IMessageEnvelope env, string? envelopeType, CancellationToken ct) {
      return Task.CompletedTask;
    }

    // Act
    var subscription = await transport.SubscribeAsync(handler, destination, CancellationToken.None);

    // Assert
    await Assert.That(subscription).IsNotNull();
  }
  [Test]
  public async Task ITransport_SendAsync_WithTimeout_ThrowsTimeoutExceptionAsync() {
    // Arrange
    var transport = _createTestTransport();
    var requestEnvelope = _createTestEnvelope();
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
  private static InProcessTransport _createTestTransport() {
    // This will use InProcessTransport once implemented
    // For now, this will fail compilation - that's expected in RED phase
    return new InProcessTransport();
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    var message = new TestMessage { Content = "Test" };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }

  // Test message types
  private sealed record TestMessage {
    public string Content { get; init; } = string.Empty;
  }

  private sealed record TestResponse {
    public string Result { get; init; } = string.Empty;
  }
}
