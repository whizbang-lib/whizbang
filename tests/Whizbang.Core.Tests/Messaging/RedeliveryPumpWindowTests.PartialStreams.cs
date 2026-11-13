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
/// #2149: every bundle of an answer claims the answered window, but a bundle that carries only part of a stream's answer
/// (the rest is in another bundle, on another page, or beyond the origin's cap) must not let the receiver mark that
/// stream's bucket as one repair cannot change. Each bundle names the streams it carries only in part.
/// </summary>
public partial class RedeliveryPumpWindowTests {
  [Test]
  public async Task PublishAnswer_AStreamSplitAcrossBundles_IsNamedPartialInEach_AWholeStreamInNoneAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 2 });
    var split = TrackedGuid.New().Value;
    var whole = TrackedGuid.New().Value;

    _ = await pump.PublishAnswerAsync(
      [_evt(split, 1, 10), _evt(split, 2, 11), _evt(split, 3, 12), _evt(whole, 1, 13)], new RedeliveryAnswerWindow(9, 20),
      topic: "inbox.whizbang.service.billing", target: "billing");

    await Assert.That(serializer.Composites.Count).IsEqualTo(3);
    var splitBundles = serializer.Composites.Where(c => c.InnerStreamIds!.Contains(split)).ToList();
    await Assert.That(splitBundles.Count).IsEqualTo(2);
    foreach (var bundle in splitBundles) {
      await Assert.That(((IIdentityPreservingComposite)bundle).AnsweredWindow!.Value.PartialStreamIds!).Contains(split)
        .Because("a bundle holding part of a stream's answer cannot say the stream had nothing missing");
    }
    var wholeBundle = serializer.Composites.Single(c => c.InnerStreamIds!.Contains(whole));
    await Assert.That(((IIdentityPreservingComposite)wholeBundle).AnsweredWindow!.Value.PartialStreamIds).IsNull()
      .Because("a stream carried whole in one bundle is covered by the window, as before");
  }

  [Test]
  public async Task PublishAnswer_AStreamTheCallerSaysContinues_IsNamedPartialInTheBundleThatCarriesItAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory());
    var open = TrackedGuid.New().Value;
    var closed = TrackedGuid.New().Value;
    var elsewhere = TrackedGuid.New().Value;

    _ = await pump.PublishAnswerAsync(
      [_evt(closed, 1, 10), _evt(open, 1, 11)],
      new RedeliveryAnswerWindow(9, 20) { PartialStreamIds = [open, elsewhere] },
      topic: "inbox.whizbang.service.billing", target: "billing");

    var openBundle = serializer.Composites.Single(c => c.InnerStreamIds!.Contains(open));
    await Assert.That(openBundle.AnsweredPartialStreamIds!).IsEquivalentTo([open])
      .Because("the stream's answer continues on the next page or stopped at the cap; a stream this bundle does not carry is not named");
    var closedBundle = serializer.Composites.Single(c => c.InnerStreamIds!.Contains(closed));
    await Assert.That(closedBundle.AnsweredPartialStreamIds).IsNull();
  }

  [Test]
  public async Task Publish_ThatAnswersNoWindow_NamesNoPartialStreamAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });
    var stream = TrackedGuid.New().Value;

    _ = await pump.PublishAsync([_evt(stream, 1, 10), _evt(stream, 2, 11)], topic: "t", target: "billing");

    await Assert.That(serializer.Composites.All(c => c.AnsweredPartialStreamIds is null)).IsTrue()
      .Because("a bundle that claims no window marks nothing by it, so it has nothing to qualify");
  }
}
