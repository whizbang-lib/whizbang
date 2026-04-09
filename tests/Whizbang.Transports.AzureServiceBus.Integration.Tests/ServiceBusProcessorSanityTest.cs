using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// CRITICAL: Tests that ServiceBusProcessor (background listener) works with Azure Service Bus Emulator.
/// This is different from the other sanity tests which use explicit receivers (ReceiveMessageAsync).
/// If this test FAILS, it confirms the emulator doesn't support processors - only receivers.
///
/// This test belongs in the library, not the samples, because it verifies the emulator
/// supports the processor pattern that Whizbang transport uses.
/// </summary>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(30_000)]  // 30s timeout
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusProcessorSanityTest(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  /// <summary>
  /// Tests that ServiceBusProcessor receives messages from generic topic-00.
  /// This is the CRITICAL test to verify processor support in the emulator.
  /// </summary>
  [Test]
  public async Task ServiceBusProcessor_ReceivesMessages_FromGenericTopicAsync() {
    const string topicName = "topic-00";
    const string subscriptionName = "sub-00-a";
    var connectionString = _fixture.ConnectionString;

    Console.WriteLine("[PROCESSOR TEST] ==========================================================");
    Console.WriteLine("[PROCESSOR TEST] CRITICAL: Testing ServiceBusProcessor with generic topic");
    Console.WriteLine($"[PROCESSOR TEST] Topic: {topicName}, Subscription: {subscriptionName}");
    Console.WriteLine("[PROCESSOR TEST] ==========================================================");

    var client = _fixture.Client;

    // Drain stale messages
    Console.WriteLine("[PROCESSOR TEST] Draining stale messages...");
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
    Console.WriteLine($"[PROCESSOR TEST] Drained {drained} stale messages");

    // Create processor (background listener)
    Console.WriteLine("[PROCESSOR TEST] Creating ServiceBusProcessor...");
    var processorOptions = new ServiceBusProcessorOptions {
      MaxConcurrentCalls = 1,
      AutoCompleteMessages = false
    };
    var processor = client.CreateProcessor(topicName, subscriptionName, processorOptions);

    // Track received messages
    var receivedMessageId = "";
    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlock when Dispose() waits for handler
    var messageReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    processor.ProcessMessageAsync += async args => {
      Console.WriteLine($"[PROCESSOR TEST] ✅ PROCESSOR RECEIVED MESSAGE: {args.Message.MessageId}");
      receivedMessageId = args.Message.MessageId;
      await args.CompleteMessageAsync(args.Message);
      messageReceived.TrySetResult(true);
    };

    processor.ProcessErrorAsync += args => {
      Console.WriteLine($"[PROCESSOR TEST] ❌ PROCESSOR ERROR: {args.Exception.Message}");
      return Task.CompletedTask;
    };

    // Start processor BEFORE sending message
    Console.WriteLine("[PROCESSOR TEST] Starting processor...");
    await processor.StartProcessingAsync();
    Console.WriteLine("[PROCESSOR TEST] ✅ Processor started!");

    // Wait a moment for processor to initialize
    await Task.Delay(1000);

    // Send test message
    Console.WriteLine("[PROCESSOR TEST] Sending test message...");
    var sender = client.CreateSender(topicName);
    var testMessageId = Guid.NewGuid().ToString();
    var message = new ServiceBusMessage("{\"test\":true}") {
      MessageId = testMessageId,
      ContentType = "application/json"
    };
    message.ApplicationProperties["EnvelopeType"] = "TestMessage";

    await sender.SendMessageAsync(message);
    Console.WriteLine($"[PROCESSOR TEST] ✅ Message sent: {testMessageId}");

    // Wait for processor to receive message (30s timeout)
    Console.WriteLine("[PROCESSOR TEST] Waiting for processor to receive message (30s timeout)...");
    var receivedTask = messageReceived.Task;
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
    var completedTask = await Task.WhenAny(receivedTask, timeoutTask);

    // Stop processor
    await processor.StopProcessingAsync();
    await processor.DisposeAsync();

    if (completedTask == timeoutTask) {
      Console.WriteLine("[PROCESSOR TEST] ❌❌❌ PROCESSOR DID NOT RECEIVE MESSAGE!");
      Console.WriteLine("[PROCESSOR TEST] This confirms Azure Service Bus Emulator does NOT support ServiceBusProcessor!");
      Console.WriteLine("[PROCESSOR TEST] The emulator only supports explicit receivers (ReceiveMessageAsync)!");
      throw new TimeoutException("ServiceBusProcessor did not receive message - emulator limitation");
    }

    Console.WriteLine($"[PROCESSOR TEST] ✅✅✅ PROCESSOR RECEIVED MESSAGE: {receivedMessageId}");
    await Assert.That(receivedMessageId).IsEqualTo(testMessageId);
  }
}
