using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Manual Azure Service Bus tests to validate SqlFilter behavior with special characters.
/// These tests use raw Azure SDK - no Whizbang framework code.
///
/// Uses pre-provisioned topic-filter-test from Config.json with:
/// - sub-namespace-filter: SqlFilter sys.Label LIKE 'jdx.contracts.chat.%'
/// - sub-all-messages: TrueFilter (receives all messages)
///
/// Key finding: Azure Service Bus SqlFilter uses 'sys.Label' NOT '[Subject]' for the
/// Subject/Label property. The [Subject] syntax does not work in SqlRuleFilter expressions.
/// See: https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-sql-filter
///
/// The '+' character in nested class type names (e.g., ContainerClass+NestedClass) DOES match
/// SqlFilter LIKE patterns correctly - no normalization needed.
/// </summary>
/// <docs>components/transports/azure-service-bus#sqlfilter-syntax</docs>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]  // 120s timeout — retry on quota exhaustion needs headroom
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ManualSubjectFilterTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  // Pre-provisioned in Config.json
  private const string TOPIC_NAME = "topic-filter-test";
  private const string FILTERED_SUBSCRIPTION = "sub-namespace-filter";  // SqlFilter: [Subject] LIKE 'jdx.contracts.chat.%'
  private const string ALL_MESSAGES_SUBSCRIPTION = "sub-all-messages";   // TrueFilter - receives all

  /// <summary>
  /// RED TEST: Validates that SqlFilter LIKE pattern matches Subject containing '+' character.
  ///
  /// Context: In JDNext, nested class commands like `ChatConversationsContracts+CreateCommand`
  /// produce routing keys like `jdx.contracts.chat.chatconversationscontracts+createcommand`.
  /// The subscription filter is `[Subject] LIKE 'jdx.contracts.chat.%'`.
  ///
  /// This test validates whether the `+` character causes matching issues.
  /// If this test FAILS, it proves `+` is the problem and we need to normalize it.
  /// </summary>
  [Test]
  public async Task SqlFilter_WithPlusInSubject_ShouldMatchLikePatternAsync() {
    var connectionString = _fixture.ConnectionString;

    Console.WriteLine("[MANUAL TEST] Testing SqlFilter LIKE pattern with '+' character in Subject...");
    Console.WriteLine($"[MANUAL TEST] Topic: {TOPIC_NAME}");
    Console.WriteLine($"[MANUAL TEST] Filtered subscription: {FILTERED_SUBSCRIPTION}");
    Console.WriteLine($"[MANUAL TEST] All-messages subscription: {ALL_MESSAGES_SUBSCRIPTION}");

    var client = _fixture.Client;

    // Drain any stale messages from previous test runs
    await _drainSubscriptionAsync(client, TOPIC_NAME, FILTERED_SUBSCRIPTION);
    await _drainSubscriptionAsync(client, TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);

    // Create sender
    var sender = client.CreateSender(TOPIC_NAME);

    // This is the exact format TransportPublishStrategy would generate for a nested class
    const string subjectWithPlus = "jdx.contracts.chat.chatconversationscontracts+createcommand";

    var message = new ServiceBusMessage("test payload for + character") {
      MessageId = Guid.NewGuid().ToString(),
      Subject = subjectWithPlus,
      ContentType = "application/json"
    };

    Console.WriteLine($"[MANUAL TEST] Publishing message with Subject: '{subjectWithPlus}'");
    Console.WriteLine($"[MANUAL TEST] MessageId: {message.MessageId}");
    await sender.SendMessageAsync(message);
    Console.WriteLine("[MANUAL TEST] Message published");

    // First, verify the message exists in the all-messages subscription (TrueFilter)
    Console.WriteLine("[MANUAL TEST] Checking all-messages subscription (should receive)...");
    var allReceiver = client.CreateReceiver(TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);
    var allMessage = await allReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

    if (allMessage != null) {
      Console.WriteLine($"[MANUAL TEST] All-messages received: {allMessage.Subject}");
      await allReceiver.CompleteMessageAsync(allMessage);
    } else {
      Console.WriteLine("[MANUAL TEST] All-messages: NO MESSAGE - emulator may have issue");
    }

    // Now check the filtered subscription - this is the real test
    Console.WriteLine("[MANUAL TEST] Checking filtered subscription (SqlFilter test)...");
    var filteredReceiver = client.CreateReceiver(TOPIC_NAME, FILTERED_SUBSCRIPTION);
    var filteredMessage = await filteredReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

    if (filteredMessage != null) {
      Console.WriteLine($"[MANUAL TEST] FILTERED subscription received: {filteredMessage.Subject}");
      Console.WriteLine("[MANUAL TEST] CONCLUSION: '+' character DOES match SqlFilter LIKE pattern");
      await filteredReceiver.CompleteMessageAsync(filteredMessage);
    } else {
      Console.WriteLine("[MANUAL TEST] FILTERED subscription: NO MESSAGE");
      Console.WriteLine("[MANUAL TEST] CONCLUSION: '+' character DOES NOT match SqlFilter LIKE pattern");
      Console.WriteLine("[MANUAL TEST] FIX REQUIRED: Normalize '+' to '.' in routing keys");
    }

    await Assert.That(filteredMessage)
      .IsNotNull()
      .Because("Message with '+' in Subject should match SqlFilter LIKE 'jdx.contracts.chat.%'. " +
               "If this fails, the '+' character is causing SqlFilter mismatch.");

    await Assert.That(filteredMessage!.Subject).IsEqualTo(subjectWithPlus);
  }

  /// <summary>
  /// Control test: Validates that SqlFilter LIKE pattern matches Subject with only dots (no '+').
  /// This should always pass - it's the baseline behavior.
  /// </summary>
  [Test]
  public async Task SqlFilter_WithDotInSubject_ShouldMatchLikePatternAsync() {
    var connectionString = _fixture.ConnectionString;

    Console.WriteLine("[MANUAL TEST] Control test: SqlFilter LIKE pattern with '.' only (no '+')...");

    var client = _fixture.Client;

    // Drain any stale messages
    await _drainSubscriptionAsync(client, TOPIC_NAME, FILTERED_SUBSCRIPTION);
    await _drainSubscriptionAsync(client, TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);

    var sender = client.CreateSender(TOPIC_NAME);

    // This is what the Subject SHOULD look like after normalizing '+' to '.'
    const string subjectWithDots = "jdx.contracts.chat.chatconversationscontracts.createcommand";

    var message = new ServiceBusMessage("test payload for . character") {
      MessageId = Guid.NewGuid().ToString(),
      Subject = subjectWithDots,
      ContentType = "application/json"
    };

    Console.WriteLine($"[MANUAL TEST] Publishing message with Subject: '{subjectWithDots}'");
    await sender.SendMessageAsync(message);
    Console.WriteLine("[MANUAL TEST] Message published");

    // Verify all-messages receives it
    var allReceiver = client.CreateReceiver(TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);
    var allMessage = await allReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (allMessage != null) {
      await allReceiver.CompleteMessageAsync(allMessage);
    }

    // Check filtered subscription
    var filteredReceiver = client.CreateReceiver(TOPIC_NAME, FILTERED_SUBSCRIPTION);
    var filteredMessage = await filteredReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

    if (filteredMessage != null) {
      Console.WriteLine("[MANUAL TEST] Control test PASSED - dots work correctly");
      await filteredReceiver.CompleteMessageAsync(filteredMessage);
    } else {
      Console.WriteLine("[MANUAL TEST] Control test FAILED - this should never happen!");
    }

    await Assert.That(filteredMessage)
      .IsNotNull()
      .Because("Control test: Subject with dots (no '+') should always match SqlFilter LIKE pattern");

    await Assert.That(filteredMessage!.Subject).IsEqualTo(subjectWithDots);
  }

  /// <summary>
  /// Additional test: Validates multiple '+' characters in Subject.
  /// Tests edge case of deeply nested classes.
  /// </summary>
  [Test]
  public async Task SqlFilter_WithMultiplePlusInSubject_ShouldMatchLikePatternAsync() {
    var connectionString = _fixture.ConnectionString;

    Console.WriteLine("[MANUAL TEST] Testing multiple '+' characters in Subject...");

    var client = _fixture.Client;

    await _drainSubscriptionAsync(client, TOPIC_NAME, FILTERED_SUBSCRIPTION);
    await _drainSubscriptionAsync(client, TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);

    var sender = client.CreateSender(TOPIC_NAME);

    // Simulates a doubly-nested class: OuterClass+InnerClass+DeepestClass
    const string subjectWithMultiplePlus = "jdx.contracts.chat.outer+inner+createcommand";

    var message = new ServiceBusMessage("test payload for multiple + characters") {
      MessageId = Guid.NewGuid().ToString(),
      Subject = subjectWithMultiplePlus,
      ContentType = "application/json"
    };

    Console.WriteLine($"[MANUAL TEST] Publishing message with Subject: '{subjectWithMultiplePlus}'");
    await sender.SendMessageAsync(message);

    // Verify all-messages receives it
    var allReceiver = client.CreateReceiver(TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);
    var allMessage = await allReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (allMessage != null) {
      await allReceiver.CompleteMessageAsync(allMessage);
    }

    // Check filtered subscription
    var filteredReceiver = client.CreateReceiver(TOPIC_NAME, FILTERED_SUBSCRIPTION);
    var filteredMessage = await filteredReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

    if (filteredMessage != null) {
      Console.WriteLine("[MANUAL TEST] Multiple '+' test PASSED");
      await filteredReceiver.CompleteMessageAsync(filteredMessage);
    } else {
      Console.WriteLine("[MANUAL TEST] Multiple '+' test FAILED");
    }

    await Assert.That(filteredMessage)
      .IsNotNull()
      .Because("Message with multiple '+' in Subject should match SqlFilter LIKE pattern");
  }

  /// <summary>
  /// Negative test: Validates that SqlFilter correctly REJECTS non-matching subjects.
  /// This proves the filter is actually being applied.
  /// </summary>
  [Test]
  public async Task SqlFilter_WithNonMatchingSubject_ShouldNotReceiveAsync() {
    var connectionString = _fixture.ConnectionString;

    Console.WriteLine("[MANUAL TEST] Negative test: Non-matching subject should NOT be received...");

    var client = _fixture.Client;

    await _drainSubscriptionAsync(client, TOPIC_NAME, FILTERED_SUBSCRIPTION);
    await _drainSubscriptionAsync(client, TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);

    var sender = client.CreateSender(TOPIC_NAME);

    // This subject does NOT match 'jdx.contracts.chat.%'
    const string nonMatchingSubject = "other.namespace.somecommand";

    var message = new ServiceBusMessage("test payload for non-matching subject") {
      MessageId = Guid.NewGuid().ToString(),
      Subject = nonMatchingSubject,
      ContentType = "application/json"
    };

    Console.WriteLine($"[MANUAL TEST] Publishing message with Subject: '{nonMatchingSubject}'");
    await sender.SendMessageAsync(message);

    // All-messages should receive it (TrueFilter)
    var allReceiver = client.CreateReceiver(TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION);
    var allMessage = await allReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

    await Assert.That(allMessage)
      .IsNotNull()
      .Because("All-messages subscription (TrueFilter) should receive any message");

    if (allMessage != null) {
      await allReceiver.CompleteMessageAsync(allMessage);
    }

    // Filtered subscription should NOT receive it (SqlFilter mismatch)
    var filteredReceiver = client.CreateReceiver(TOPIC_NAME, FILTERED_SUBSCRIPTION);
    var filteredMessage = await filteredReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(3));

    if (filteredMessage == null) {
      Console.WriteLine("[MANUAL TEST] Negative test PASSED - filter correctly rejected non-matching subject");
    } else {
      Console.WriteLine("[MANUAL TEST] Negative test FAILED - filter did NOT reject non-matching subject!");
      await filteredReceiver.CompleteMessageAsync(filteredMessage);
    }

    await Assert.That(filteredMessage)
      .IsNull()
      .Because("Filtered subscription should NOT receive messages with non-matching Subject");
  }

  /// <summary>
  /// Helper method to drain stale messages from a subscription.
  /// </summary>
  private static async Task _drainSubscriptionAsync(ServiceBusClient client, string topicName, string subscriptionName) {
    // Retry on ConnectionsQuotaExceeded — emulator has limited connections
    for (var attempt = 0; attempt < 3; attempt++) {
      try {
        var receiver = client.CreateReceiver(topicName, subscriptionName);
        var drained = 0;
        for (var i = 0; i < 100; i++) {
          var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
          if (msg == null) {
            break;
          }

          await receiver.CompleteMessageAsync(msg);
          drained++;
        }
        await receiver.DisposeAsync();
        if (drained > 0) {
          Console.WriteLine($"[MANUAL TEST] Drained {drained} stale messages from {subscriptionName}");
        }
        return; // Success
      } catch (Azure.Messaging.ServiceBus.ServiceBusException ex) when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.QuotaExceeded) {
        Console.WriteLine($"[MANUAL TEST] Connection quota exceeded on attempt {attempt + 1}, waiting 2s...");
        await Task.Delay(2000);
      }
    }
  }
}
