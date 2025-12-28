using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Infrastructure;

/// <summary>
/// Sanity tests for Azure Service Bus Emulator - verifies emulator is working correctly
/// without any Whizbang library code. Uses only Azure SDK directly.
///
/// These tests use DirectServiceBusEmulatorFixture which runs the emulator via docker-compose
/// instead of Aspire, avoiding Aspire's memory/stability issues.
///
/// CRITICAL FINDING (2025-12-28): The Azure Service Bus Emulator has an OOM bug when using
/// custom Config.json on ARM64 (Mac M-series). The emulator crashes during "Triggering Entity Sync"
/// even with 4GB memory and just ONE topic/subscription. The default built-in configuration works
/// reliably. This affects both Aspire and direct docker-compose setups.
/// </summary>
[NotInParallel]
public class ServiceBusEmulatorSanityTests {
  private static DirectServiceBusEmulatorFixture? _fixture;

  [Before(Test)]
  public async Task SetupAsync() {
    if (_fixture == null) {
      _fixture = new DirectServiceBusEmulatorFixture();
      await _fixture.InitializeAsync();
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  /// <summary>
  /// Most basic test: Send a message to a topic and receive it from a subscription.
  /// Uses only Azure Service Bus SDK - no Whizbang code involved.
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_SendAndReceive_WorksAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    Console.WriteLine("[SANITY TEST] Starting Azure Service Bus Emulator sanity test...");

    // Use the default topic from emulator's built-in Config.json
    var topicName = "topic.1";
    var subscriptionName = "subscription.1";

    Console.WriteLine($"[SANITY TEST] Using topic: {topicName}, subscription: {subscriptionName}");

    // Create Service Bus client directly from connection string
    var connectionString = fixture.ServiceBusConnectionString;
    await using var client = new ServiceBusClient(connectionString);

    // Create a sender
    Console.WriteLine("[SANITY TEST] Creating sender...");
    var sender = client.CreateSender(topicName);

    // Create a test message with all required properties for subscription.1's CorrelationFilter
    var testMessageBody = $"Sanity test message at {DateTimeOffset.UtcNow:O}";
    var message = new ServiceBusMessage(testMessageBody) {
      MessageId = "msgid1",
      Subject = "subject1",
      CorrelationId = "id1",
      ContentType = "application/text",
      ReplyTo = "someQueue",
      SessionId = "session1",
      ReplyToSessionId = "sessionId",
      To = "xyz"
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
    await Assert.That(receivedMessage.Subject).IsEqualTo("subject1");
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    // Complete the message
    await receiver.CompleteMessageAsync(receivedMessage);
    Console.WriteLine("[SANITY TEST] ✅ Message completed successfully!");

    Console.WriteLine("[SANITY TEST] ✅✅✅ ALL CHECKS PASSED - Emulator is working correctly!");
  }

  /// <summary>
  /// Tests creating a NEW topic and subscription dynamically (not from pool).
  /// Verifies emulator can handle dynamic provisioning.
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_DynamicTopicCreation_WorksAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    Console.WriteLine("[SANITY TEST] Testing dynamic topic creation...");

    var connectionString = fixture.ServiceBusConnectionString;
    var adminClient = new ServiceBusAdministrationClient(connectionString);

    // Create unique topic/subscription names
    var testId = Guid.NewGuid().ToString("N")[..8];
    var topicName = $"sanity-test-{testId}";
    var subscriptionName = $"sanity-sub-{testId}";

    Console.WriteLine($"[SANITY TEST] Creating topic: {topicName}");

    try {
      // Create topic
      var createTopicTask = adminClient.CreateTopicAsync(topicName);
      var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
      var completedTask = await Task.WhenAny(createTopicTask, timeoutTask);

      if (completedTask == timeoutTask) {
        Console.WriteLine("[SANITY TEST] ❌ CreateTopicAsync timed out after 30 seconds");
        throw new TimeoutException("CreateTopicAsync timed out - emulator not responding");
      }

      await createTopicTask;
      Console.WriteLine($"[SANITY TEST] ✅ Topic created: {topicName}");

      // Create subscription with TrueFilter
      Console.WriteLine($"[SANITY TEST] Creating subscription: {subscriptionName}");
      var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName);
      var ruleOptions = new CreateRuleOptions("$Default", new TrueRuleFilter());

      var createSubTask = adminClient.CreateSubscriptionAsync(subscriptionOptions, ruleOptions);
      timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
      completedTask = await Task.WhenAny(createSubTask, timeoutTask);

      if (completedTask == timeoutTask) {
        Console.WriteLine("[SANITY TEST] ❌ CreateSubscriptionAsync timed out after 30 seconds");
        throw new TimeoutException("CreateSubscriptionAsync timed out - emulator not responding");
      }

      await createSubTask;
      Console.WriteLine($"[SANITY TEST] ✅ Subscription created: {subscriptionName}");

      // Now send and receive like the first test
      await using var client = new ServiceBusClient(connectionString);
      var sender = client.CreateSender(topicName);

      var message = new ServiceBusMessage("Dynamic topic test") {
        MessageId = Guid.NewGuid().ToString()
      };

      Console.WriteLine("[SANITY TEST] Sending message to dynamic topic...");
      await sender.SendMessageAsync(message);
      Console.WriteLine("[SANITY TEST] ✅ Message sent!");

      var receiver = client.CreateReceiver(topicName, subscriptionName);
      var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

      await Assert.That(receivedMessage).IsNotNull();
      await Assert.That(receivedMessage!.MessageId).IsEqualTo(message.MessageId);
      await receiver.CompleteMessageAsync(receivedMessage);

      Console.WriteLine("[SANITY TEST] ✅✅✅ Dynamic topic creation works!");

    } finally {
      // Cleanup
      try {
        Console.WriteLine("[SANITY TEST] Cleaning up test topic...");
        await adminClient.DeleteTopicAsync(topicName);
        Console.WriteLine("[SANITY TEST] Test topic deleted");
      } catch (Exception ex) {
        Console.WriteLine($"[SANITY TEST] Warning: Failed to delete test topic: {ex.Message}");
      }
    }
  }
}
