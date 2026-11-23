// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Inside one handling each message that starts a stream under a minted id takes the handling's next ordinal once
/// (#2537): a send that generates its stream more than once (before the receptor runs, again on its envelope) keeps
/// one stream, and a redelivered handling, a new envelope, starts again at ordinal zero.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/GeneratedStreamAssignments.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class GeneratedStreamAssignmentsTests {
  private sealed record Probe(string Name);

  [Test]
  public async Task GetOrAdd_TheSameMessageTwice_DerivesOnceAndKeepsItsStreamAsync() {
    var handling = _handling();
    var message = new Probe("a");
    var ordinals = new List<int>();

    var first = GeneratedStreamAssignments.GetOrAdd(handling, message, o => { ordinals.Add(o); return _streamFor(o); });
    var again = GeneratedStreamAssignments.GetOrAdd(handling, message, o => { ordinals.Add(o); return _streamFor(o); });

    await Assert.That(again).IsEqualTo(first)
      .Because("the stored copy and the local run of one send must name one stream");
    await Assert.That(ordinals).IsEquivalentTo([0]);
  }

  [Test]
  public async Task GetOrAdd_AnotherMessage_TakesTheNextOrdinalAsync() {
    var handling = _handling();

    var first = GeneratedStreamAssignments.GetOrAdd(handling, new Probe("a"), _streamFor);
    var second = GeneratedStreamAssignments.GetOrAdd(handling, new Probe("a"), _streamFor);

    await Assert.That(first).IsEqualTo(_streamFor(0));
    await Assert.That(second).IsEqualTo(_streamFor(1))
      .Because("two messages are two sends, even when they are equal records: identity is the instance");
  }

  [Test]
  public async Task GetOrAdd_ANewHandling_StartsAgainAtZeroAsync() {
    var source = MessageId.New();

    var firstRun = GeneratedStreamAssignments.GetOrAdd(_handling(source), new Probe("a"), _streamFor);
    var redelivery = GeneratedStreamAssignments.GetOrAdd(_handling(source), new Probe("a"), _streamFor);

    await Assert.That(redelivery).IsEqualTo(firstRun)
      .Because("a redelivery materializes a fresh envelope and sends again from ordinal zero");
  }

  [Test]
  public async Task GetOrAdd_NullArguments_AreRefusedAsync() {
    await Assert.That(() => GeneratedStreamAssignments.GetOrAdd(null!, new Probe("a"), _streamFor)).Throws<ArgumentNullException>();
    await Assert.That(() => GeneratedStreamAssignments.GetOrAdd(_handling(), null!, _streamFor)).Throws<ArgumentNullException>();
    await Assert.That(() => GeneratedStreamAssignments.GetOrAdd(_handling(), new Probe("a"), null!)).Throws<ArgumentNullException>();
  }

  // ---- outside a handling: minted once per message instance (#2539) ----

  [Test]
  public async Task GetOrMint_TheSameMessageTwice_MintsOnceAsync() {
    var message = new Probe("a");

    var first = GeneratedStreamAssignments.GetOrMint(message);
    var again = GeneratedStreamAssignments.GetOrMint(message);

    await Assert.That(again).IsEqualTo(first)
      .Because("the envelopes one send builds for one message must name one stream");
    await Assert.That(first.Version).IsEqualTo(7);
  }

  [Test]
  public async Task GetOrMint_AnotherMessage_MintsAnotherStreamAsync() {
    var first = GeneratedStreamAssignments.GetOrMint(new Probe("a"));
    var second = GeneratedStreamAssignments.GetOrMint(new Probe("a"));

    await Assert.That(second).IsNotEqualTo(first)
      .Because("outside a handling each message still starts a fresh stream: identity is the instance");
  }

  [Test]
  public async Task GetOrMint_ANullMessage_IsRefusedAsync() {
    await Assert.That(() => GeneratedStreamAssignments.GetOrMint(null!)).Throws<ArgumentNullException>();
  }

  private static Guid _streamFor(int ordinal) => new(ordinal + 1, 0, 0, new byte[8]);

  private static MessageEnvelope<object> _handling(MessageId? id = null) => new() {
    MessageId = id ?? MessageId.New(),
    Payload = new Probe("cause"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}
