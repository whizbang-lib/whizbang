// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Containers;
using Whizbang.Testing.Transport;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable TUnit0023 // Disposable field should be disposed in cleanup method

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// Integration tests verifying that ScopeDelta (security context) and the dispatch mark survive
/// RabbitMQ transport publish → subscribe round-trip.
/// </summary>
/// <remarks>
/// Uses real RabbitMQ via Testcontainers. Verifies envelope.GetCurrentScope()
/// returns correct UserId/TenantId after deserialization on the consumer side.
/// </remarks>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public sealed class ScopeContextTransportIntegrationTests : IAsyncDisposable {
  private IConnection? _connection;
  private RabbitMQChannelPool? _channelPool;
  private RabbitMQTransport? _transport;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedRabbitMqContainer.InitializeOrSkipAsync();

    var factory = new ConnectionFactory {
      Uri = new Uri(SharedRabbitMqContainer.ConnectionString)
    };
    _connection = await factory.CreateConnectionAsync();
    _channelPool = new RabbitMQChannelPool(_connection, maxChannels: 5);

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new RabbitMQOptions();

    _transport = new RabbitMQTransport(
      _connection,
      jsonOptions,
      _channelPool,
      options,
      logger: null
    );

    await _transport.InitializeAsync();
  }

  [After(Test)]
  public Task CleanupAsync() {
    var transport = _transport;
    var channelPool = _channelPool;
    var connection = _connection;

    _transport = null;
    _channelPool = null;
    _connection = null;

    _ = Task.Run(async () => {
      try {
        if (transport != null) {
          await transport.DisposeAsync();
        }
        channelPool?.Dispose();
        if (connection != null) {
          await connection.CloseAsync();
          connection.Dispose();
        }
      } catch {
        // Ignore cleanup errors
      }
    }, CancellationToken.None);

    return Task.CompletedTask;
  }

  // ========================================
  // Tests
  // ========================================

  [Test]
  [Timeout(90000)]
  public async Task Publish_WithUserScope_ReceivePreservesScope_RabbitMQAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var envelope = _createEnvelopeWithScope("user-123", "tenant-456");
    var destination = _createUniqueDestination();

    // Use MessageAwaiter to capture the full envelope
    var awaiter = new MessageAwaiter<IMessageEnvelope>(
      e => e // Capture the entire envelope
    );
    var subscription = await _transport!.SubscribeAsync(
      awaiter.Handler,
      destination,
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Act
      await _transport.PublishAsync(envelope, _toPublishDestination(destination), cancellationToken: cancellationToken);

      // Assert
      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(received).IsNotNull();

      var scope = received.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("ScopeDelta should survive RabbitMQ publish → subscribe round-trip");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("user-123");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("tenant-456");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task Publish_WithSystemScope_ReceivePreservesScope_RabbitMQAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var envelope = _createEnvelopeWithScope("SYSTEM", "*");
    var destination = _createUniqueDestination();

    // Accept ONLY the envelope this test published. The topic is shared by nine test
    // classes, and draining before the test cannot close the gap: a message whose broker
    // lock expires after the drain window becomes visible again afterwards and lands in
    // this awaiter. Taking "whatever arrives first" then asserts against a foreign
    // envelope — which fails as scope == null, looking like a transport bug rather than
    // contamination. Matching on MessageId makes the test independent of channel state.
    var awaiter = new MessageAwaiter<IMessageEnvelope>(
      e => e, filter: e => e.MessageId == envelope.MessageId);
    var subscription = await _transport!.SubscribeAsync(
      awaiter.Handler,
      destination,
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Act
      await _transport.PublishAsync(envelope, _toPublishDestination(destination), cancellationToken: cancellationToken);

      // Assert
      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      var scope = received.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("SYSTEM scope should survive RabbitMQ round-trip");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("SYSTEM");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("*");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task Publish_WithNoScope_ReceiveHasNoScope_RabbitMQAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - envelope with NO ScopeDelta
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("no-scope-test"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
    var destination = _createUniqueDestination();

    // Accept ONLY the envelope this test published. The topic is shared by nine test
    // classes, and draining before the test cannot close the gap: a message whose broker
    // lock expires after the drain window becomes visible again afterwards and lands in
    // this awaiter. Taking "whatever arrives first" then asserts against a foreign
    // envelope — which fails as scope == null, looking like a transport bug rather than
    // contamination. Matching on MessageId makes the test independent of channel state.
    var awaiter = new MessageAwaiter<IMessageEnvelope>(
      e => e, filter: e => e.MessageId == envelope.MessageId);
    var subscription = await _transport!.SubscribeAsync(
      awaiter.Handler,
      destination,
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Act
      await _transport.PublishAsync(envelope, _toPublishDestination(destination), cancellationToken: cancellationToken);

      // Assert
      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      var scope = received.GetCurrentScope();
      await Assert.That(scope).IsNull()
        .Because("Envelope without ScopeDelta should have null scope after round-trip");
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // The dispatch mark (#1410 follow-up)
  // ========================================

  /// <summary>
  /// The mark the sender's dispatch puts on a copy survives the broker: a copy the sender already handled
  /// says so (its own inbox does not run it again), and a copy whose local run was deferred to its
  /// schedule says that (its own inbox runs it when it comes back).
  /// </summary>
  /// <remarks>The subscription is consuming once SubscribeAsync returns: it awaits the bind and the consume.</remarks>
  [Test]
  [Timeout(90000)]
  [Arguments(DispatchModes.Both, false)]
  [Arguments(DispatchModes.Outbox, true)]
  [Arguments(DispatchModes.Outbox, false)]
  public async Task Publish_WithADispatchMark_ReceivePreservesTheMark_RabbitMQAsync(
    DispatchModes mode, bool localDeferred, CancellationToken cancellationToken
  ) {
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("dispatch-mark"),
      DispatchContext = new MessageDispatchContext { Mode = mode, Source = MessageSource.Outbox, LocalDeferred = localDeferred },
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var destination = _createUniqueDestination();
    var awaiter = new MessageAwaiter<IMessageEnvelope>(e => e, filter: e => e.MessageId == envelope.MessageId);
    var subscription = await _transport!.SubscribeAsync(awaiter.Handler, destination, cancellationToken);

    try {
      await _transport.PublishAsync(envelope, _toPublishDestination(destination), cancellationToken: cancellationToken);

      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(received.DispatchContext.Mode).IsEqualTo(mode)
        .Because("whether the sender already handled the copy must survive the broker");
      await Assert.That(received.DispatchContext.LocalDeferred).IsEqualTo(localDeferred)
        .Because("whether the sender handles the copy when it comes back must survive the broker");
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // Helpers
  // ========================================

  private static MessageEnvelope<TestMessage> _createEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("scope-test"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ]
    };
  }

  private static TransportDestination _createUniqueDestination() {
    var address = $"scope-test-{Guid.NewGuid():N}";
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse($"\"scope-sub-{Guid.NewGuid():N}\"").RootElement.Clone()
    };
    return new TransportDestination(address, null, metadata);
  }

  private static TransportDestination _toPublishDestination(TransportDestination subscribeDestination) {
    return new TransportDestination(subscribeDestination.Address, null);
  }

  public ValueTask DisposeAsync() {
    var transport = _transport;
    var channelPool = _channelPool;
    var connection = _connection;

    _transport = null;
    _channelPool = null;
    _connection = null;

    _ = Task.Run(async () => {
      try {
        if (transport != null) {
          await transport.DisposeAsync();
        }
        channelPool?.Dispose();
        if (connection != null) {
          await connection.CloseAsync();
          connection.Dispose();
        }
      } catch {
        // Ignore cleanup errors
      }
    }, CancellationToken.None);

    return ValueTask.CompletedTask;
  }
}
