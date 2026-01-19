using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Infrastructure;

/// <summary>
/// Sanity tests for Azure Service Bus Emulator - verifies emulator is working correctly
/// without any Whizbang library code. Uses only Azure SDK directly.
///
/// Uses ClassDataSource for ServiceBus emulator fixture injection.
/// ServiceBus initialization happens BEFORE tests run via TUnit's fixture lifecycle.
/// All tests use topic-00 and topic-01.
/// </summary>
[Timeout(20_000)]  // 20s timeout for fail-fast (ServiceBus pre-initialized via ClassDataSource)
[ClassDataSource<ServiceBusBatchFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusEmulatorSanityTests(ServiceBusBatchFixtureSource fixtureSource) {
  private readonly ServiceBusBatchFixture _serviceBusFixture = fixtureSource.ServiceBusFixture;

  /// <summary>
  /// Most basic test: Send a message to a topic and receive it from a subscription.
  /// Uses only Azure Service Bus SDK - no Whizbang code involved.
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_SendAndReceive_WorksAsync() {
    // All tests use the same topics (topic-00)
    var topicName = "topic-00";
    var subscriptionName = "sub-00-a";
    var connectionString = _serviceBusFixture.ConnectionString;

    Console.WriteLine("[SANITY TEST] Starting Azure Service Bus Emulator sanity test...");
    Console.WriteLine($"[SANITY TEST] Using topic: {topicName}, subscription: {subscriptionName}");

    // Create Service Bus client directly from connection string
    await using var client = new ServiceBusClient(connectionString);

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

    // Send with timeout
    var sendTask = sender.SendMessageAsync(message);
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
    var completedTask = await Task.WhenAny(sendTask, timeoutTask);

    if (completedTask == timeoutTask) {
      Console.WriteLine("[SANITY TEST] ❌ FAILED: SendMessageAsync timed out after 30 seconds");
      throw new TimeoutException("SendMessageAsync timed out after 30 seconds - emulator not responding");
    }

    await sendTask; // Re-await to propagate exceptions
    Console.WriteLine("[SANITY TEST] ✅ Message sent successfully!");

    // Now try to receive it
    Console.WriteLine("[SANITY TEST] Creating receiver...");
    var receiver = client.CreateReceiver(topicName, subscriptionName);

    Console.WriteLine("[SANITY TEST] Waiting to receive message (30s timeout)...");
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    Console.WriteLine($"[SANITY TEST] ✅ Received message: {receivedMessage!.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    // Complete the message
    await receiver.CompleteMessageAsync(receivedMessage);
    Console.WriteLine("[SANITY TEST] ✅ Message completed successfully!");

    Console.WriteLine("[SANITY TEST] ✅✅✅ ALL CHECKS PASSED - Emulator is working correctly!");
  }

  /// <summary>
  /// Tests sending/receiving on a different generic topic (second topic set for this test index).
  /// Verifies multiple generic topics work correctly.
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_InventoryTopic_WorksAsync() {
    // All tests use the same topics (topic-01)
    var topicName = "topic-01";
    var subscriptionName = "sub-01-a";
    var connectionString = _serviceBusFixture.ConnectionString;

    Console.WriteLine("[SANITY TEST] Testing second generic topic...");
    Console.WriteLine($"[SANITY TEST] Using topic: {topicName}, subscription: {subscriptionName}");

    await using var client = new ServiceBusClient(connectionString);

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
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    await Assert.That(receivedMessage!.MessageId).IsEqualTo(message.MessageId);
    await receiver.CompleteMessageAsync(receivedMessage);

    Console.WriteLine("[SANITY TEST] ✅✅✅ Generic topic works!");
  }
}
