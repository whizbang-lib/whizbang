using Azure.Messaging.ServiceBus;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Infrastructure;

/// <summary>
/// Tests using the EXTRACTED default config explicitly mounted.
/// This verifies we can mount a config file without causing OOM, as long as it matches the working default.
/// If this passes, we can try making minimal modifications to the config.
/// </summary>
[NotInParallel]
public class ServiceBusEmulatorExtractedConfigTests {
  private static DirectServiceBusEmulatorFixture? _fixture;

  [Before(Test)]
  public async Task SetupAsync() {
    if (_fixture == null) {
      _fixture = new DirectServiceBusEmulatorFixture("Config-Default.json");
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

  [Test]
  public async Task ServiceBusEmulator_WithExtractedConfig_WorksAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    Console.WriteLine("[EXTRACTED CONFIG] Starting test with extracted Config-Default.json...");

    // Use the same default entities as the built-in config
    var topicName = "topic.1";
    var subscriptionName = "subscription.1";

    Console.WriteLine($"[EXTRACTED CONFIG] Using topic: {topicName}, subscription: {subscriptionName}");

    var connectionString = fixture.ServiceBusConnectionString;
    await using var client = new ServiceBusClient(connectionString);

    // Create a sender
    Console.WriteLine("[EXTRACTED CONFIG] Creating sender...");
    var sender = client.CreateSender(topicName);

    // Create a test message with required properties for CorrelationFilter
    var testMessageBody = $"Test with extracted config at {DateTimeOffset.UtcNow:O}";
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

    Console.WriteLine($"[EXTRACTED CONFIG] Sending message: {message.MessageId}");

    // Send with timeout
    var sendTask = sender.SendMessageAsync(message);
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
    var completedTask = await Task.WhenAny(sendTask, timeoutTask);

    if (completedTask == timeoutTask) {
      Console.WriteLine("[EXTRACTED CONFIG] ❌ FAILED: SendMessageAsync timed out after 30 seconds");
      throw new TimeoutException("SendMessageAsync timed out - emulator not responding");
    }

    await sendTask;
    Console.WriteLine("[EXTRACTED CONFIG] ✅ Message sent successfully!");

    // Receive message
    Console.WriteLine("[EXTRACTED CONFIG] Creating receiver...");
    var receiver = client.CreateReceiver(topicName, subscriptionName);

    Console.WriteLine("[EXTRACTED CONFIG] Waiting to receive message (30s timeout)...");
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    Console.WriteLine($"[EXTRACTED CONFIG] ✅ Received message: {receivedMessage!.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await Assert.That(receivedMessage.Subject).IsEqualTo("subject1");
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    // Complete the message
    await receiver.CompleteMessageAsync(receivedMessage);
    Console.WriteLine("[EXTRACTED CONFIG] ✅ Message completed successfully!");

    Console.WriteLine("[EXTRACTED CONFIG] ✅✅✅ SUCCESS! Extracted config works without OOM!");
  }

  /// <summary>
  /// CRITICAL TEST: Tests with MODIFIED config (renamed topic.1 → products).
  /// This is the moment of truth - will renaming entities trigger OOM?
  /// </summary>
  [Test]
  public async Task ServiceBusEmulator_WithModifiedConfig_RenamedTopic_WorksAsync() {
    // Create a new fixture with the modified config
    await using var modifiedFixture = new DirectServiceBusEmulatorFixture("Config-Modified.json");

    Console.WriteLine("[MODIFIED CONFIG] 🔥 CRITICAL TEST: Starting with renamed topic (products)...");
    await modifiedFixture.InitializeAsync();

    // Use the renamed entities
    var topicName = "products";
    var subscriptionName = "products-worker";

    Console.WriteLine($"[MODIFIED CONFIG] Using topic: {topicName}, subscription: {subscriptionName}");

    var connectionString = modifiedFixture.ServiceBusConnectionString;
    await using var client = new ServiceBusClient(connectionString);

    // Create a sender
    Console.WriteLine("[MODIFIED CONFIG] Creating sender...");
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

    Console.WriteLine($"[MODIFIED CONFIG] Sending message: {message.MessageId}");

    // Send with timeout
    var sendTask = sender.SendMessageAsync(message);
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
    var completedTask = await Task.WhenAny(sendTask, timeoutTask);

    if (completedTask == timeoutTask) {
      Console.WriteLine("[MODIFIED CONFIG] ❌ FAILED: SendMessageAsync timed out after 30 seconds");
      throw new TimeoutException("SendMessageAsync timed out - emulator not responding");
    }

    await sendTask;
    Console.WriteLine("[MODIFIED CONFIG] ✅ Message sent successfully!");

    // Receive message
    Console.WriteLine("[MODIFIED CONFIG] Creating receiver...");
    var receiver = client.CreateReceiver(topicName, subscriptionName);

    Console.WriteLine("[MODIFIED CONFIG] Waiting to receive message (30s timeout)...");
    var receivedMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));

    await Assert.That(receivedMessage).IsNotNull();
    Console.WriteLine($"[MODIFIED CONFIG] ✅ Received message: {receivedMessage!.MessageId}");

    await Assert.That(receivedMessage.MessageId).IsEqualTo(message.MessageId);
    await Assert.That(receivedMessage.Subject).IsEqualTo("subject1");
    await Assert.That(receivedMessage.Body.ToString()).IsEqualTo(testMessageBody);

    // Complete the message
    await receiver.CompleteMessageAsync(receivedMessage);
    Console.WriteLine("[MODIFIED CONFIG] ✅ Message completed successfully!");

    Console.WriteLine("[MODIFIED CONFIG] 🎉🎉🎉 BREAKTHROUGH! Renamed topic works without OOM!");
  }
}
