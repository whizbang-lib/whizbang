// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2311: an answer spans several bundles, and the receiver judges a whole-type answer as a whole only if it can tell
/// the answer's last bundle from the others. Every bundle of an answer says more of it follows, except the last.
/// </summary>
public partial class RedeliveryPumpWindowTests {
  [Test]
  public async Task PublishAnswer_EveryBundleButTheLast_SaysTheAnswerContinuesAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });

    var published = await pump.PublishAnswerAsync(
      [_evt(TrackedGuid.New().Value, 1, 10), _evt(TrackedGuid.New().Value, 1, 11), _evt(TrackedGuid.New().Value, 1, 12)],
      new RedeliveryAnswerWindow(9, 20), topic: "inbox.whizbang.service.billing", target: "billing");

    await Assert.That(published).IsEqualTo(3);
    await Assert.That(string.Join(",", serializer.Composites.Select(c => c.AnswerContinues))).IsEqualTo("True,True,False")
      .Because("a bundle that changed nothing must not let the receiver mark the type lane while bundles of the answer remain");
    await Assert.That(((IIdentityPreservingComposite)serializer.Composites[0]).AnsweredWindow!.Value.Continues).IsTrue()
      .Because("the receiver reads it from the bundle's answered window");
    await Assert.That(((IIdentityPreservingComposite)serializer.Composites[2]).AnsweredWindow!.Value.Continues).IsFalse();
  }

  [Test]
  public async Task Publish_ThatAnswersNoWindow_SaysNothingContinuesAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });
    var stream = TrackedGuid.New().Value;

    _ = await pump.PublishAsync([_evt(stream, 1, 10), _evt(stream, 2, 11)], topic: "t", target: "billing");

    await Assert.That(serializer.Composites.All(c => !c.AnswerContinues)).IsTrue()
      .Because("a bundle that answers no window marks no type lane, so it has nothing to say about the rest");
  }
  [Test]
  public async Task BeginAnswer_OverSeveralPublishes_HoldsBackEachLastBundle_AndEndsOnlyOnCompleteAsync() {
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport, serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });
    var (a, b, c) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);
    var answer = pump.BeginAnswer("inbox.whizbang.service.billing", "billing", TrackedGuid.New().Value);

    var firstCall = await answer.PublishAsync([_evt(a, 1, 10), _evt(b, 1, 11)], new RedeliveryAnswerWindow(9, 20));
    var sentAfterTheFirst = serializer.Composites.Count;
    var secondCall = await answer.PublishAsync(
      [_evt(c, 1, 12)], new RedeliveryAnswerWindow(9, 20) { PartialStreamIds = [c] });
    var sentAfterTheSecond = serializer.Composites.Count;
    var completed = await answer.CompleteAsync();
    var completedAgain = await answer.CompleteAsync();

    await Assert.That((firstCall, sentAfterTheFirst)).IsEqualTo((1, 1))
      .Because("the selection's last bundle waits: only the next selection, or the end, says whether the answer goes on");
    await Assert.That((secondCall, sentAfterTheSecond)).IsEqualTo((1, 2))
      .Because("the held bundle goes first, and this selection's only bundle is held in its turn");
    await Assert.That((completed, completedAgain)).IsEqualTo((1, 0));
    await Assert.That(string.Join(",", serializer.Composites.Select(x => x.InnerStreamIds![0]))).IsEqualTo($"{a},{b},{c}")
      .Because("holding a bundle back never reorders the answer");
    await Assert.That(string.Join(",", serializer.Composites.Select(x => x.AnswerContinues))).IsEqualTo("True,True,False");
    var last = serializer.Composites[2];
    await Assert.That(last.StreamId).IsEqualTo(c);
    await Assert.That(last.AnsweredPartialStreamIds).IsEquivalentTo([c])
      .Because("the last bundle is the held one built again: it keeps every other thing it said");
    await Assert.That((last.AnswersWindow, last.AnsweredFromCommitSequence, last.AnsweredToCommitSequence))
      .IsEqualTo((true, (long?)9, (long?)20));
    await Assert.That(transport.Published.Count).IsEqualTo(3);
  }

  [Test]
  public async Task BeginAnswer_ThatPublishesNothing_EndsWithoutABundleAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory());
    var answer = pump.BeginAnswer("t", target: null);

    var published = await answer.PublishAsync([], new RedeliveryAnswerWindow(null, null));
    var completed = await answer.CompleteAsync();

    await Assert.That((published, completed)).IsEqualTo((0, 0));
    await Assert.That(serializer.Composites).IsEmpty();
  }

  [Test]
  public async Task BeginAnswer_RefusesAMissingTopic_AndAPublishOfNoListAsync() {
    var pump = new RedeliveryPump(new CaptureTransport(), new CaptureSerializer(), _instance(), new CompositeFactory());

    await Assert.That(() => pump.BeginAnswer(" ", "billing")).Throws<ArgumentException>();
    await Assert.That(async () => await pump.BeginAnswer("t", "billing").PublishAsync(null!, new RedeliveryAnswerWindow(null, null)))
      .Throws<ArgumentNullException>();
  }
}
