// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
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

  private sealed record EventProbe(string Name) : IEvent;

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

  [Test]
  public async Task GetOrAdd_AnEventAndACommand_TakeOrdinalsOfTheirOwnAsync() {
    var handling = _handling();

    var invokedEvent = GeneratedStreamAssignments.GetOrAdd(handling, new EventProbe("e"), _streamFor);
    var sentCommand = GeneratedStreamAssignments.GetOrAdd(handling, new Probe("c"), _streamFor);
    var secondEvent = GeneratedStreamAssignments.GetOrAdd(handling, new EventProbe("e"), _streamFor);

    await Assert.That(invokedEvent).IsEqualTo(_streamFor(0));
    await Assert.That(sentCommand).IsEqualTo(_streamFor(0))
      .Because("invoking an event inside a handling (#2552) must not move the streams of the commands it sends (#2537)");
    await Assert.That(secondEvent).IsEqualTo(_streamFor(1));
  }

  // ---- a stream derived from the message's own id (#2552) ----

  [Test]
  public async Task Assign_InsideAHandling_IsWhatGetOrAddReturns_WithoutTakingAnOrdinalAsync() {
    var handling = _handling();
    var published = new EventProbe("published");
    var assigned = (Guid)TrackedGuid.New();

    GeneratedStreamAssignments.Assign(handling, published, assigned);
    var later = GeneratedStreamAssignments.GetOrAdd(handling, published, _streamFor);
    var next = GeneratedStreamAssignments.GetOrAdd(handling, new EventProbe("invoked"), _streamFor);

    await Assert.That(later).IsEqualTo(assigned)
      .Because("the envelope a publish's local run builds must name the stream the publish derived from the event's id");
    await Assert.That(next).IsEqualTo(_streamFor(0))
      .Because("an assigned stream is not one the handling derived, so it takes no ordinal");
  }

  [Test]
  public async Task Assign_Again_ReplacesTheStreamAsync() {
    var handling = _handling();
    var published = new EventProbe("published");
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();

    GeneratedStreamAssignments.Assign(handling, published, first);
    GeneratedStreamAssignments.Assign(handling, published, second);
    GeneratedStreamAssignments.Assign(null, published, first);
    GeneratedStreamAssignments.Assign(null, published, second);

    await Assert.That(GeneratedStreamAssignments.GetOrAdd(handling, published, _streamFor)).IsEqualTo(second)
      .Because("publishing the instance again is a new message under a new id, which starts the stream that id derives");
    await Assert.That(GeneratedStreamAssignments.GetOrMint(published)).IsEqualTo(second);
  }

  [Test]
  public async Task Assign_OutsideAHandling_IsWhatGetOrMintReturnsAsync() {
    var published = new EventProbe("published");
    var assigned = (Guid)TrackedGuid.New();

    GeneratedStreamAssignments.Assign(null, published, assigned);

    await Assert.That(GeneratedStreamAssignments.GetOrMint(published)).IsEqualTo(assigned)
      .Because("one publish gives one event one stream, everywhere it goes (#2539)");
  }

  [Test]
  public async Task Assign_ANullMessage_IsRefusedAsync() {
    await Assert.That(() => GeneratedStreamAssignments.Assign(_handling(), null!, (Guid)TrackedGuid.New())).Throws<ArgumentNullException>();
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
