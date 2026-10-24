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
/// <para>Manual Azure Service Bus tests to validate SqlFilter behavior with special characters.
/// These tests use raw Azure SDK - no Whizbang framework code.</para>
///
/// <para>Uses pre-provisioned topic-filter-test from Config.json with:
/// - sub-namespace-filter: SqlFilter sys.Label LIKE 'app.contracts.chat.%'
/// - sub-all-messages: TrueFilter (receives all messages)</para>
///
/// <para>Key finding: Azure Service Bus SqlFilter uses 'sys.Label' NOT '[Subject]' for the
/// Subject/Label property. The [Subject] syntax does not work in SqlRuleFilter expressions.
/// See: https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-sql-filter</para>
///
/// <para>The '+' character in nested class type names (e.g., ContainerClass+NestedClass) DOES match
/// SqlFilter LIKE patterns correctly - no normalization needed.</para>
/// </summary>
/// <docs>components/transports/azure-service-bus#sqlfilter-syntax</docs>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ManualSubjectFilterTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Emulator;

  // Pre-provisioned in Config.json
  private const string TOPIC_NAME = "topic-filter-test";
  private const string FILTERED_SUBSCRIPTION = "sub-namespace-filter";  // SqlFilter: [Subject] LIKE 'app.contracts.chat.%'
  private const string ALL_MESSAGES_SUBSCRIPTION = "sub-all-messages";   // TrueFilter - receives all

  // How long a test waits for its own message to come off a subscription before failing.
  private static readonly TimeSpan _budget = TimeSpan.FromSeconds(30);

  /// <summary>
  /// <para>RED TEST: Validates that SqlFilter LIKE pattern matches Subject containing '+' character.</para>
  ///
  /// <para>Context: In a consumer application, nested class commands like `ChatConversationsContracts+CreateCommand`
  /// produce routing keys like `app.contracts.chat.chatconversationscontracts+createcommand`.
  /// The subscription filter is `[Subject] LIKE 'app.contracts.chat.%'`.</para>
  ///
  /// <para>This test validates whether the `+` character causes matching issues.
  /// If this test FAILS, it proves `+` is the problem and we need to normalize it.</para>
  /// </summary>
  [Test]
  public async Task SqlFilter_WithPlusInSubject_ShouldMatchLikePatternAsync(CancellationToken cancellationToken) {
    // This is the exact format TransportPublishStrategy would generate for a nested class
    const string subjectWithPlus = "app.contracts.chat.chatconversationscontracts+createcommand";

    var filteredMessage = await _publishAndReceiveFilteredAsync(subjectWithPlus, cancellationToken);

    await Assert.That(filteredMessage.Subject).IsEqualTo(subjectWithPlus)
      .Because("Message with '+' in Subject should match SqlFilter LIKE 'app.contracts.chat.%'. " +
               "If this fails, the '+' character is causing SqlFilter mismatch.");
  }

  /// <summary>
  /// Control test: Validates that SqlFilter LIKE pattern matches Subject with only dots (no '+').
  /// This should always pass - it's the baseline behavior.
  /// </summary>
  [Test]
  public async Task SqlFilter_WithDotInSubject_ShouldMatchLikePatternAsync(CancellationToken cancellationToken) {
    // This is what the Subject SHOULD look like after normalizing '+' to '.'
    const string subjectWithDots = "app.contracts.chat.chatconversationscontracts.createcommand";

    var filteredMessage = await _publishAndReceiveFilteredAsync(subjectWithDots, cancellationToken);

    await Assert.That(filteredMessage.Subject).IsEqualTo(subjectWithDots)
      .Because("Control test: Subject with dots (no '+') should always match SqlFilter LIKE pattern");
  }

  /// <summary>
  /// Additional test: Validates multiple '+' characters in Subject.
  /// Tests edge case of deeply nested classes.
  /// </summary>
  [Test]
  public async Task SqlFilter_WithMultiplePlusInSubject_ShouldMatchLikePatternAsync(CancellationToken cancellationToken) {
    // Simulates a doubly-nested class: OuterClass+InnerClass+DeepestClass
    const string subjectWithMultiplePlus = "app.contracts.chat.outer+inner+createcommand";

    var filteredMessage = await _publishAndReceiveFilteredAsync(subjectWithMultiplePlus, cancellationToken);

    await Assert.That(filteredMessage.Subject).IsEqualTo(subjectWithMultiplePlus)
      .Because("Message with multiple '+' in Subject should match SqlFilter LIKE pattern");
  }

  /// <summary>
  /// Negative test: Validates that SqlFilter correctly REJECTS non-matching subjects.
  /// This proves the filter is actually being applied.
  /// </summary>
  /// <remarks>
  /// Absence is proven against a sentinel, not a timeout: the broker applies a subscription's
  /// filter when the topic fans a message out, so once a matching sentinel published AFTER the
  /// non-matching message has reached the filtered subscription, the non-matching message has
  /// already been either admitted or rejected there. Everything received ahead of the sentinel,
  /// and everything still on the subscription after it, must not include the non-matching message.
  /// </remarks>
  [Test]
  public async Task SqlFilter_WithNonMatchingSubject_ShouldNotReceiveAsync(CancellationToken cancellationToken) {
    var client = _fixture.Client;

    // This subject does NOT match 'app.contracts.chat.%'
    const string nonMatchingSubject = "other.namespace.somecommand";
    var nonMatchingId = Guid.NewGuid().ToString();
    var sentinelId = Guid.NewGuid().ToString();

    await using (var sender = client.CreateSender(TOPIC_NAME)) {
      await sender.SendMessageAsync(_message(nonMatchingId, nonMatchingSubject), cancellationToken);
      await sender.SendMessageAsync(_message(sentinelId, "app.contracts.chat.sentinel"), cancellationToken);
    }

    // All-messages should receive it (TrueFilter): the message really was published.
    await using (var allReceiver = client.CreateReceiver(TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION)) {
      var (allMessage, _) = await CorrelatedReceive.UntilAsync(allReceiver, nonMatchingId, _budget, cancellationToken);
      await Assert.That(allMessage.Subject).IsEqualTo(nonMatchingSubject)
        .Because("All-messages subscription (TrueFilter) should receive any message");
    }

    // Filtered subscription should NOT receive it (SqlFilter mismatch)
    await using var filteredReceiver = client.CreateReceiver(TOPIC_NAME, FILTERED_SUBSCRIPTION);
    var (_, receivedBeforeSentinel) =
      await CorrelatedReceive.UntilAsync(filteredReceiver, sentinelId, _budget, cancellationToken);
    var stillQueued = await filteredReceiver.PeekMessagesAsync(maxMessages: 250, fromSequenceNumber: 0, cancellationToken);

    await Assert.That(receivedBeforeSentinel).DoesNotContain(nonMatchingId)
      .Because("Filtered subscription should NOT receive messages with non-matching Subject");
    await Assert.That(stillQueued.Select(m => m.MessageId)).DoesNotContain(nonMatchingId)
      .Because("Filtered subscription should NOT receive messages with non-matching Subject");
  }

  /// <summary>
  /// Publishes one message with <paramref name="subject"/>, confirms the TrueFilter subscription got
  /// it, and returns it as the filtered subscription delivers it (failing if it never does).
  /// </summary>
  private async Task<ServiceBusReceivedMessage> _publishAndReceiveFilteredAsync(string subject, CancellationToken cancellationToken) {
    var client = _fixture.Client;
    var messageId = Guid.NewGuid().ToString();

    await using (var sender = client.CreateSender(TOPIC_NAME)) {
      await sender.SendMessageAsync(_message(messageId, subject), cancellationToken);
    }

    await using (var allReceiver = client.CreateReceiver(TOPIC_NAME, ALL_MESSAGES_SUBSCRIPTION)) {
      var (allMessage, _) = await CorrelatedReceive.UntilAsync(allReceiver, messageId, _budget, cancellationToken);
      await Assert.That(allMessage.Subject).IsEqualTo(subject)
        .Because("All-messages subscription (TrueFilter) should receive any message");
    }

    await using var filteredReceiver = client.CreateReceiver(TOPIC_NAME, FILTERED_SUBSCRIPTION);
    var (filteredMessage, _) = await CorrelatedReceive.UntilAsync(filteredReceiver, messageId, _budget, cancellationToken);
    return filteredMessage;
  }

  private static ServiceBusMessage _message(string messageId, string subject) =>
    new($"test payload for subject {subject}") {
      MessageId = messageId,
      Subject = subject,
      ContentType = "application/json"
    };
}
