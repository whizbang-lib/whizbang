using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for transport lifecycle - initialization and readiness tracking.
/// </summary>
public class TransportLifecycleTests {
  [Test]
  public async Task ITransport_BeforeInitialization_IsInitializedShouldBeFalseAsync() {
    // Arrange
    var transport = new TestTransport();

    // Act & Assert
    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("Transport should not be initialized before InitializeAsync is called");
  }

  [Test]
  public async Task ITransport_AfterInitialization_IsInitializedShouldBeTrueAsync() {
    // Arrange
    var transport = new TestTransport();

    // Act
    await transport.InitializeAsync();

    // Assert
    await Assert.That(transport.IsInitialized).IsTrue()
      .Because("Transport should be marked as initialized after successful InitializeAsync");
  }

  [Test]
  public async Task ITransport_InitializeAsync_ShouldBeIdempotentAsync() {
    // Arrange
    var transport = new TestTransport();

    // Act - Call initialize multiple times
    await transport.InitializeAsync();
    await transport.InitializeAsync();
    await transport.InitializeAsync();

    // Assert - Should still be initialized without errors
    await Assert.That(transport.IsInitialized).IsTrue()
      .Because("Multiple calls to InitializeAsync should not cause errors");
    await Assert.That(transport.InitializationCount).IsEqualTo(1)
      .Because("InitializeAsync should only perform initialization once");
  }

  [Test]
  public async Task ITransport_InitializeAsync_WithCancellation_ShouldThrowAsync() {
    // Arrange
    var transport = new TestTransport();
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await transport.InitializeAsync(cts.Token))
      .Throws<OperationCanceledException>()
      .Because("InitializeAsync should respect cancellation token");

    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("Transport should not be marked as initialized when initialization was cancelled");
  }

  [Test]
  public async Task ITransport_InitializeAsync_WithFailure_ShouldNotMarkInitializedAsync() {
    // Arrange
    var transport = new FailingInitializationTransport();

    // Act & Assert
    await Assert.That(async () => await transport.InitializeAsync())
      .Throws<InvalidOperationException>()
      .Because("InitializeAsync should propagate initialization errors");

    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("Transport should not be marked as initialized when initialization fails");
  }

  [Test]
  public async Task ITransport_InitializeAsync_AfterFailure_CanRetryAsync() {
    // Arrange
    var transport = new RetryableInitializationTransport();

    // Act - First call fails
    await Assert.That(async () => await transport.InitializeAsync())
      .Throws<InvalidOperationException>();

    // Fix the condition
    transport.ShouldFail = false;

    // Retry initialization
    await transport.InitializeAsync();

    // Assert - Should now be initialized
    await Assert.That(transport.IsInitialized).IsTrue()
      .Because("Transport should be able to retry initialization after failure");
  }
}

/// <summary>
/// Test transport implementation for lifecycle testing.
/// </summary>
internal sealed class TestTransport : ITransport {
  private bool _isInitialized;
  private int _initializationCount;

  public bool IsInitialized => _isInitialized;
  public int InitializationCount => _initializationCount;

  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();

    // Idempotent - only initialize once
    if (_isInitialized) {
      return;
    }

    // Simulate async initialization work
    await Task.Delay(10, cancellationToken);

    _initializationCount++;
    _isInitialized = true;
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
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

/// <summary>
/// Test transport that fails during initialization.
/// </summary>
internal sealed class FailingInitializationTransport : ITransport {
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value - intentional for test
  private bool _isInitialized; // Intentionally never set - initialization always fails
#pragma warning restore CS0649

  public bool IsInitialized => _isInitialized;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    throw new InvalidOperationException("Failed to connect to transport");
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
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

/// <summary>
/// Test transport that can fail and then succeed on retry.
/// </summary>
internal sealed class RetryableInitializationTransport : ITransport {
  private bool _isInitialized;

  public bool IsInitialized => _isInitialized;
  public bool ShouldFail { get; set; } = true;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (ShouldFail) {
      throw new InvalidOperationException("Failed to connect to transport");
    }

    _isInitialized = true;
    return Task.CompletedTask;
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
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
