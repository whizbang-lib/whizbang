using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportCapabilities enum.
/// Defines the expected capabilities flags for transports.
/// Following TDD: These tests are written BEFORE the enum implementation.
/// </summary>
public class TransportCapabilitiesTests {
  [Test]
  public async Task TransportCapabilities_HasNoneValueAsync() {
    // Arrange & Act
    var none = TransportCapabilities.None;

    // Assert
    await Assert.That((int)none).IsEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_HasRequestResponseAsync() {
    // Arrange & Act
    var capability = TransportCapabilities.RequestResponse;

    // Assert
    await Assert.That((int)capability).IsNotEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_HasPublishSubscribeAsync() {
    // Arrange & Act
    var capability = TransportCapabilities.PublishSubscribe;

    // Assert
    await Assert.That((int)capability).IsNotEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_HasStreamingAsync() {
    // Arrange & Act
    var capability = TransportCapabilities.Streaming;

    // Assert
    await Assert.That((int)capability).IsNotEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_HasReliableAsync() {
    // Arrange & Act
    var capability = TransportCapabilities.Reliable;

    // Assert
    await Assert.That((int)capability).IsNotEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_HasOrderedAsync() {
    // Arrange & Act
    var capability = TransportCapabilities.Ordered;

    // Assert
    await Assert.That((int)capability).IsNotEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_HasExactlyOnceAsync() {
    // Arrange & Act
    var capability = TransportCapabilities.ExactlyOnce;

    // Assert
    await Assert.That((int)capability).IsNotEqualTo(0);
  }

  [Test]
  public async Task TransportCapabilities_CanCombineFlagsAsync() {
    // Arrange & Act
    var combined = TransportCapabilities.RequestResponse | TransportCapabilities.Reliable;

    // Assert
    await Assert.That(combined.HasFlag(TransportCapabilities.RequestResponse)).IsTrue();
    await Assert.That(combined.HasFlag(TransportCapabilities.Reliable)).IsTrue();
    await Assert.That(combined.HasFlag(TransportCapabilities.Streaming)).IsFalse();
  }

  [Test]
  public async Task TransportCapabilities_AllFlag_ContainsAllCapabilitiesAsync() {
    // Arrange & Act
    var all = TransportCapabilities.All;

    // Assert
    await Assert.That(all.HasFlag(TransportCapabilities.RequestResponse)).IsTrue();
    await Assert.That(all.HasFlag(TransportCapabilities.PublishSubscribe)).IsTrue();
    await Assert.That(all.HasFlag(TransportCapabilities.Streaming)).IsTrue();
    await Assert.That(all.HasFlag(TransportCapabilities.Reliable)).IsTrue();
    await Assert.That(all.HasFlag(TransportCapabilities.Ordered)).IsTrue();
    await Assert.That(all.HasFlag(TransportCapabilities.ExactlyOnce)).IsTrue();
  }
}
