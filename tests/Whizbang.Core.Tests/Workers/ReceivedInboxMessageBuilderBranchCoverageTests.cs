// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="ReceivedInboxMessageBuilder.ExtractStreamId"/> for an envelope that arrived with no hop
/// list at all. The envelope's JSON constructor takes the hop list as given, so a producer that wrote
/// a null hop list yields an envelope whose hops are null rather than empty.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
[Category("Workers")]
public class ReceivedInboxMessageBuilderBranchCoverageTests {

  [Test]
  public async Task ExtractStreamId_EnvelopeWithNullHops_FallsBackToTheMessageIdAsync() {
    var messageId = MessageId.New();
    // The same constructor JSON deserialization uses; it stores the hop list it is handed.
    var envelope = new MessageEnvelope<JsonElement>(messageId, JsonDocument.Parse("{}").RootElement, hops: null!);

    var streamId = ReceivedInboxMessageBuilder.ExtractStreamId(envelope);

    await Assert.That(streamId).IsEqualTo(messageId.Value)
      .Because("every received message needs a stream, so a missing hop list means the message is its own stream");
  }

  private sealed class ByMessageIdHook(MessageId expected) : Whizbang.Core.Priority.IPriorityReceiveHook {
    public int Order => 0;
    public int Classify(Whizbang.Core.Priority.PriorityReceiveContext context) =>
      context.Envelope.MessageId == expected ? 42 : context.Declared;
  }

  /// <summary>
  /// A receive hook classifies the message actually received: it is shown that envelope, so a rule that
  /// reads the payload or the hops decides by this message and not by its type alone.
  /// </summary>
  [Test]
  public async Task Classify_WithAReceiveHook_ShowsItTheReceivedEnvelopeAsync() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement>(messageId, JsonDocument.Parse("{}").RootElement, hops: []);
    using var scope = new ServiceCollection()
      .AddSingleton(new Whizbang.Core.Priority.PriorityHookChain([], [new ByMessageIdHook(messageId)], []))
      .BuildServiceProvider();

    var classified = ReceivedInboxMessageBuilder.Classify(scope, envelope, "Probe.Event");

    await Assert.That(classified).IsEqualTo(42);
  }
}
