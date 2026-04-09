using System.Text.Json;
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
using Whizbang.Testing.Transport;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Integration tests verifying that ScopeDelta (security context) survives
/// Azure Service Bus transport publish → subscribe round-trip.
/// </summary>
/// <remarks>
/// Uses real Azure Service Bus emulator via Testcontainers. Verifies envelope.GetCurrentScope()
/// returns correct UserId/TenantId after deserialization on the consumer side.
/// </remarks>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public sealed class ScopeContextTransportIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  // ========================================
  // Tests
  // ========================================

  [Test]
  public async Task Publish_WithUserScope_ReceivePreservesScope_ServiceBusAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var envelope = _createEnvelopeWithScope("user-123", "tenant-456");
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var transport = await _createTransportAsync();
    var awaiter = new MessageAwaiter<IMessageEnvelope>(e => e);
    var warmupSignal = new SignalAwaiter();
    var warmupId = SubscriptionWarmup.GenerateWarmupId();

    var subscription = await transport.SubscribeAsync(
      async (env, envType, ct) => {
        if (env is MessageEnvelope<TestMessage> testEnv &&
            testEnv.Payload.Content.Contains(warmupId)) {
          warmupSignal.Signal();
        } else {
          await awaiter.Handler(env, envType, ct);
        }
      },
      new TransportDestination("topic-00", "sub-00-a"),
      cancellationToken
    );

    try {
      // Warmup subscription
      var publishDest = new TransportDestination("topic-00");
      await SubscriptionWarmup.WarmupAsync(
        transport,
        publishDest,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupSignal,
        cancellationToken: cancellationToken
      );

      // Act
      await transport.PublishAsync(envelope, publishDest, cancellationToken: cancellationToken);

      // Assert
      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
      await Assert.That(received).IsNotNull();

      var scope = received.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("ScopeDelta should survive Azure Service Bus publish → subscribe round-trip");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("user-123");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("tenant-456");
    } finally {
      subscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  [Test]
  public async Task Publish_WithSystemScope_ReceivePreservesScope_ServiceBusAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var envelope = _createEnvelopeWithScope("SYSTEM", "*");
    await _drainMessagesAsync("topic-01", "sub-01-a");

    var transport = await _createTransportAsync();
    var awaiter = new MessageAwaiter<IMessageEnvelope>(e => e);
    var warmupSignal = new SignalAwaiter();
    var warmupId = SubscriptionWarmup.GenerateWarmupId();

    var subscription = await transport.SubscribeAsync(
      async (env, envType, ct) => {
        if (env is MessageEnvelope<TestMessage> testEnv &&
            testEnv.Payload.Content.Contains(warmupId)) {
          warmupSignal.Signal();
        } else {
          await awaiter.Handler(env, envType, ct);
        }
      },
      new TransportDestination("topic-01", "sub-01-a"),
      cancellationToken
    );

    try {
      var publishDest = new TransportDestination("topic-01");
      await SubscriptionWarmup.WarmupAsync(
        transport,
        publishDest,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupSignal,
        cancellationToken: cancellationToken
      );

      // Act
      await transport.PublishAsync(envelope, publishDest, cancellationToken: cancellationToken);

      // Assert
      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
      var scope = received.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("SYSTEM scope should survive Azure Service Bus round-trip");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("SYSTEM");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("*");
    } finally {
      subscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  [Test]
  public async Task Publish_WithNoScope_ReceiveHasNoScope_ServiceBusAsync(
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
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())
          }
        }
      ]
    };
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var transport = await _createTransportAsync();
    var awaiter = new MessageAwaiter<IMessageEnvelope>(e => e);
    var warmupSignal = new SignalAwaiter();
    var warmupId = SubscriptionWarmup.GenerateWarmupId();

    var subscription = await transport.SubscribeAsync(
      async (env, envType, ct) => {
        if (env is MessageEnvelope<TestMessage> testEnv &&
            testEnv.Payload.Content.Contains(warmupId)) {
          warmupSignal.Signal();
        } else {
          await awaiter.Handler(env, envType, ct);
        }
      },
      new TransportDestination("topic-00", "sub-00-a"),
      cancellationToken
    );

    try {
      var publishDest = new TransportDestination("topic-00");
      await SubscriptionWarmup.WarmupAsync(
        transport,
        publishDest,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupSignal,
        cancellationToken: cancellationToken
      );

      // Act
      await transport.PublishAsync(envelope, publishDest, cancellationToken: cancellationToken);

      // Assert
      var received = await awaiter.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
      var scope = received.GetCurrentScope();
      await Assert.That(scope).IsNull()
        .Because("Envelope without ScopeDelta should have null scope after round-trip");
    } finally {
      subscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  // ========================================
  // Helpers
  // ========================================

  private async Task<AzureServiceBusTransport> _createTransportAsync() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions,
      new AzureServiceBusOptions { EnableSessions = false }
    );
    await transport.InitializeAsync();
    return transport;
  }

  private async Task _drainMessagesAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      for (var i = 0; i < 100; i++) {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (msg == null) {
          break;
        }
        await receiver.CompleteMessageAsync(msg);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

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
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithContent(string content) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())
          }
        }
      ]
    };
  }
}
