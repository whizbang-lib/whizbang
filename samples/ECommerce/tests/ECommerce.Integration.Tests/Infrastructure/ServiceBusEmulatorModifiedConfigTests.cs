using Azure.Messaging.ServiceBus;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Infrastructure;

/// <summary>
/// CRITICAL TEST: Tests with MODIFIED config (renamed entities).
/// This is the moment of truth - will renaming entities trigger OOM?
///
/// This class has NO Before/After hooks to avoid conflicts.
/// Each test manages its own fixture lifecycle.
/// </summary>
[NotInParallel]
public class ServiceBusEmulatorModifiedConfigTests {

  [Test]
  public async Task ServiceBusEmulator_WithRenamedTopic_WorksAsync() {
    // Create fixture with the modified config
    await using var fixture = new DirectServiceBusEmulatorFixture(5672, "Config-Modified.json");

    Console.WriteLine("[MODIFIED] 🔥 CRITICAL TEST: Starting with renamed topic (products)...");
    await fixture.InitializeAsync();

    // Use the renamed entities
    var topicName = "products";
    var subscriptionName = "products-worker";

    Console.WriteLine($"[MODIFIED] Using topic: {topicName}, subscription: {subscriptionName}");

    var connectionString = fixture.ServiceBusConnectionString;
    await using var client = new ServiceBusClient(connectionString);

    // Create a sender
    Console.WriteLine("[MODIFIED] Creating sender...");
    var sender = client.CreateSender(topicName);

    // Create a test message with required properties
    var testMessageBody = $"Test with modified config at {DateTimeOffset.UtcNow:O}";
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

    Console.WriteLine($"[MODIFIED] Sending message: {message.MessageId}");

    // Send with timeout
    var sendTask = sender.SendMessageAsync(message);
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
    var completedTask = await Task.WhenAny(sendTask, timeoutTask);

    if (completedTask == timeoutTask) {
      Console.WriteLine("[MODIFIED] ❌ FAILED: SendMessageAsync timed out after 30 seconds");
      throw new TimeoutException("SendMessageAsync timed out - emulator not responding");
    }

    await sendTask;
    Console.WriteLine("[MODIFIED] ✅ Message sent successfully!");

    // Receive message
    Console.WriteLine("[MODIFIED] Creating receiver...");
    var receiver = client.CreateReceiver(topicName, subscriptionName);

    Console.WriteLine("[MODIFIED] Waiting to receive message (30s timeout)...");
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    Console.WriteLine($"[MODIFIED] ✅ Received message: {receivedMessage!.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await Assert.That(receivedMessage.Subject).IsEqualTo("subject1");
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    // Complete the message
    await receiver.CompleteMessageAsync(receivedMessage);
    Console.WriteLine("[MODIFIED] ✅ Message completed successfully!");

    Console.WriteLine("[MODIFIED] 🎉🎉🎉 BREAKTHROUGH! Renamed topic works without OOM!");
  }

  [Test]
  public async Task ServiceBusEmulator_WithTrueFilter_AcceptsAnyMessageAsync() {
    // Create fixture with TrueFilter config - should accept ANY message
    await using var fixture = new DirectServiceBusEmulatorFixture(5672, "Config-TrueFilter.json");

    Console.WriteLine("[TRUEFILTER] 🔥 Testing TrueFilter with renamed entities...");
    await fixture.InitializeAsync();

    // Use the renamed entities
    var topicName = "products";
    var subscriptionName = "products-worker";

    Console.WriteLine($"[TRUEFILTER] Using topic: {topicName}, subscription: {subscriptionName}");

    var connectionString = fixture.ServiceBusConnectionString;
    await using var client = new ServiceBusClient(connectionString);

    // Create a sender
    Console.WriteLine("[TRUEFILTER] Creating sender...");
    var sender = client.CreateSender(topicName);

    // Create a SIMPLE test message (no correlation properties required)
    var testMessageBody = $"Simple test message at {DateTimeOffset.UtcNow:O}";
    var message = new ServiceBusMessage(testMessageBody) {
      MessageId = Guid.NewGuid().ToString()
    };

    Console.WriteLine($"[TRUEFILTER] Sending simple message: {message.MessageId}");

    // Send with timeout
    var sendTask = sender.SendMessageAsync(message);
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
    var completedTask = await Task.WhenAny(sendTask, timeoutTask);

    if (completedTask == timeoutTask) {
      Console.WriteLine("[TRUEFILTER] ❌ FAILED: SendMessageAsync timed out after 30 seconds");
      throw new TimeoutException("SendMessageAsync timed out - emulator not responding");
    }

    await sendTask;
    Console.WriteLine("[TRUEFILTER] ✅ Message sent successfully!");

    // Receive message
    Console.WriteLine("[TRUEFILTER] Creating receiver...");
    var receiver = client.CreateReceiver(topicName, subscriptionName);

    Console.WriteLine("[TRUEFILTER] Waiting to receive message (30s timeout)...");
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    Console.WriteLine($"[TRUEFILTER] ✅ Received message: {receivedMessage!.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    // Complete the message
    await receiver.CompleteMessageAsync(receivedMessage);
    Console.WriteLine("[TRUEFILTER] ✅ Message completed successfully!");

    Console.WriteLine("[TRUEFILTER] 🎉🎉🎉 SUCCESS! TrueFilter with renamed entities works perfectly!");
  }

  [Test]
  public async Task ServiceBusEmulator_WithTrueFilter_InventoryTopic_WorksAsync() {
    // Test the inventory topic with TrueFilter
    await using var fixture = new DirectServiceBusEmulatorFixture(5672, "Config-TrueFilter.json");

    Console.WriteLine("[INVENTORY] 🔥 Testing inventory topic with TrueFilter...");
    await fixture.InitializeAsync();

    var topicName = "inventory";
    var subscriptionName = "inventory-worker";

    Console.WriteLine($"[INVENTORY] Using topic: {topicName}, subscription: {subscriptionName}");

    var connectionString = fixture.ServiceBusConnectionString;
    await using var client = new ServiceBusClient(connectionString);

    var sender = client.CreateSender(topicName);

    var testMessageBody = $"Inventory test message at {DateTimeOffset.UtcNow:O}";
    var message = new ServiceBusMessage(testMessageBody) {
      MessageId = Guid.NewGuid().ToString()
    };

    Console.WriteLine($"[INVENTORY] Sending message: {message.MessageId}");

    await sender.SendMessageAsync(message);
    Console.WriteLine("[INVENTORY] ✅ Message sent!");

    var receiver = client.CreateReceiver(topicName, subscriptionName);
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    Console.WriteLine($"[INVENTORY] ✅ Received message: {receivedMessage!.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await receiver.CompleteMessageAsync(receivedMessage);

    Console.WriteLine("[INVENTORY] 🎉🎉🎉 SUCCESS! Inventory topic works perfectly!");
  }
}
