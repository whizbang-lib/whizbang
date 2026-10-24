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
/// <para>CRITICAL: Tests that ServiceBusProcessor (background listener) works with Azure Service Bus Emulator.
/// This is different from the other sanity tests which use explicit receivers (ReceiveMessageAsync).
/// If this test FAILS, it confirms the emulator doesn't support processors - only receivers.</para>
///
/// <para>This test belongs in the library, not the samples, because it verifies the emulator
/// supports the processor pattern that Whizbang transport uses.</para>
/// </summary>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(30_000)]  // 30s timeout
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusProcessorSanityTest(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Emulator;

  /// <summary>
  /// Tests that ServiceBusProcessor receives messages from generic topic-00.
  /// This is the CRITICAL test to verify processor support in the emulator.
  /// </summary>
  [Test]
  public async Task ServiceBusProcessor_ReceivesMessages_FromGenericTopicAsync() {
    const string topicName = "topic-00";
    const string subscriptionName = "sub-00-a";

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
    await using var processor = client.CreateProcessor(topicName, subscriptionName, processorOptions);

    // The subscription is shared, so the processor completes whatever it gets but signals only on
    // THIS test's message: a stray from another test must neither satisfy nor fail the wait.
    var testMessageId = Guid.NewGuid().ToString();
    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlock when Dispose() waits for handler
    var messageReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    processor.ProcessMessageAsync += async args => {
      Console.WriteLine($"[PROCESSOR TEST] PROCESSOR RECEIVED MESSAGE: {args.Message.MessageId}");
      await args.CompleteMessageAsync(args.Message);
      if (args.Message.MessageId == testMessageId) {
        messageReceived.TrySetResult(args.Message.MessageId);
      }
    };

    processor.ProcessErrorAsync += args => {
      Console.WriteLine($"[PROCESSOR TEST] PROCESSOR ERROR: {args.Exception.Message}");
      return Task.CompletedTask;
    };

    // Start processor BEFORE sending message. No settle wait: the subscription is durable, so the
    // message waits on the broker until the processor's receive link takes it.
    Console.WriteLine("[PROCESSOR TEST] Starting processor...");
    await processor.StartProcessingAsync();
    Console.WriteLine("[PROCESSOR TEST] Processor started!");

    string receivedMessageId;
    try {
      // Send test message
      Console.WriteLine("[PROCESSOR TEST] Sending test message...");
      await using (var sender = client.CreateSender(topicName)) {
        var message = new ServiceBusMessage("{\"test\":true}") {
          MessageId = testMessageId,
          ContentType = "application/json"
        };
        message.ApplicationProperties["EnvelopeType"] = "TestMessage";
        await sender.SendMessageAsync(message);
      }
      Console.WriteLine($"[PROCESSOR TEST] Message sent: {testMessageId}");

      // Wait for the processor to receive THIS message
      Console.WriteLine("[PROCESSOR TEST] Waiting for processor to receive message (20s budget, inside the 30s test timeout)...");
      try {
        receivedMessageId = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));
      } catch (TimeoutException ex) {
        Console.WriteLine("[PROCESSOR TEST] PROCESSOR DID NOT RECEIVE MESSAGE!");
        throw new TimeoutException("ServiceBusProcessor did not receive message - emulator limitation", ex);
      }
    } finally {
      await processor.StopProcessingAsync();
    }

    Console.WriteLine($"[PROCESSOR TEST] PROCESSOR RECEIVED MESSAGE: {receivedMessageId}");
    await Assert.That(receivedMessageId).IsEqualTo(testMessageId);
  }
}
