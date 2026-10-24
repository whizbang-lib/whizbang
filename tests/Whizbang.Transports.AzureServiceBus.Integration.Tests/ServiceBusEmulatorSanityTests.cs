// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// <para>Sanity tests for Azure Service Bus Emulator - verifies emulator is working correctly
/// without any Whizbang library code. Uses only Azure SDK directly.</para>
///
/// <para>These tests belong in the library, not the samples, because they verify the emulator
/// works correctly before testing Whizbang transport implementation.</para>
/// </summary>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(20_000)]  // 20s timeout for fail-fast (ServiceBus pre-initialized via ClassDataSource)
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusEmulatorSanityTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Emulator;

  /// <summary>
  /// Most basic test: Send a message to a topic and receive it from a subscription.
  /// Uses only Azure Service Bus SDK - no Whizbang code involved.
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_SendAndReceive_WorksAsync() {
    // All tests use the same topics (topic-00)
    const string topicName = "topic-00";
    const string subscriptionName = "sub-00-a";
    _ = _fixture.ConnectionString;

    Console.WriteLine("[SANITY TEST] Starting Azure Service Bus Emulator sanity test...");
    Console.WriteLine($"[SANITY TEST] Using topic: {topicName}, subscription: {subscriptionName}");

    // Create Service Bus client directly from connection string
    var client = _fixture.Client;

    // Drain stale messages before test (warmup messages remain from initialization)
    Console.WriteLine("[SANITY TEST] Draining stale messages...");
    var drainReceiver = client.CreateReceiver(topicName, subscriptionName);
    var drained = 0;
    for (var i = 0; i < 100; i++) {
      var msg = await drainReceiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
      if (msg == null) {
        break;
      }

      await drainReceiver.CompleteMessageAsync(msg);
      drained++;
    }
    await drainReceiver.DisposeAsync();
    if (drained > 0) {
      Console.WriteLine($"[SANITY TEST] Drained {drained} stale messages");
    }

    // Create a sender
    Console.WriteLine("[SANITY TEST] Creating sender...");
    var sender = client.CreateSender(topicName);

    // Create a simple test message (TrueFilter accepts any message)
    var testMessageBody = $"Sanity test message at {DateTimeOffset.UtcNow:O}";
    var message = new ServiceBusMessage(testMessageBody) {
      MessageId = Guid.NewGuid().ToString(),
      ContentType = "application/json"
    };

    Console.WriteLine($"[SANITY TEST] Sending message: {message.MessageId}");

    // Send, bounded: a send that never completes means the emulator is not responding
    try {
      await sender.SendMessageAsync(message).WaitAsync(TimeSpan.FromSeconds(30));
    } catch (TimeoutException ex) {
      Console.WriteLine("[SANITY TEST] ❌ FAILED: SendMessageAsync timed out after 30 seconds");
      throw new TimeoutException("SendMessageAsync timed out after 30 seconds - emulator not responding", ex);
    }
    Console.WriteLine("[SANITY TEST] ✅ Message sent successfully!");

    // Now try to receive it
    Console.WriteLine("[SANITY TEST] Creating receiver...");
    var receiver = client.CreateReceiver(topicName, subscriptionName);

    // The subscription is shared: receive until THIS message arrives (completing it and any stray)
    Console.WriteLine("[SANITY TEST] Waiting to receive message (30s timeout)...");
    var (receivedMessage, _) = await CorrelatedReceive.UntilAsync(receiver, message.MessageId, TimeSpan.FromSeconds(30));
    Console.WriteLine($"[SANITY TEST] ✅ Received and completed message: {receivedMessage.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    Console.WriteLine("[SANITY TEST] ✅✅✅ ALL CHECKS PASSED - Emulator is working correctly!");
  }

  /// <summary>
  /// Tests sending/receiving on a different generic topic (second topic set for this test index).
  /// Verifies multiple generic topics work correctly.
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_InventoryTopic_WorksAsync() {
    // All tests use the same topics (topic-01)
    const string topicName = "topic-01";
    const string subscriptionName = "sub-01-a";
    _ = _fixture.ConnectionString;

    Console.WriteLine("[SANITY TEST] Testing second generic topic...");
    Console.WriteLine($"[SANITY TEST] Using topic: {topicName}, subscription: {subscriptionName}");

    var client = _fixture.Client;

    // Drain stale messages before test (warmup messages remain from initialization)
    Console.WriteLine("[SANITY TEST] Draining stale messages...");
    var drainReceiver = client.CreateReceiver(topicName, subscriptionName);
    var drained = 0;
    for (var i = 0; i < 100; i++) {
      var msg = await drainReceiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
      if (msg == null) {
        break;
      }

      await drainReceiver.CompleteMessageAsync(msg);
      drained++;
    }
    await drainReceiver.DisposeAsync();
    if (drained > 0) {
      Console.WriteLine($"[SANITY TEST] Drained {drained} stale messages");
    }

    var sender = client.CreateSender(topicName);

    var message = new ServiceBusMessage("Inventory topic test") {
      MessageId = Guid.NewGuid().ToString(),
      ContentType = "application/json"
    };

    Console.WriteLine("[SANITY TEST] Sending message to generic topic...");
    await sender.SendMessageAsync(message);
    Console.WriteLine("[SANITY TEST] ✅ Message sent!");

    var receiver = client.CreateReceiver(topicName, subscriptionName);
    var (receivedMessage, _) = await CorrelatedReceive.UntilAsync(receiver, message.MessageId, TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);

    Console.WriteLine("[SANITY TEST] ✅✅✅ Generic topic works!");
  }
}
