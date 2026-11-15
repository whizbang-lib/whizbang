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
}
