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
/// #2340: the bundles of one answer can ride several broker sessions and commit in any order, so each names its answer
/// and its place in it, and the last says how many bundles the answer has; the receiver then judges the answer once
/// every bundle is in. #2341: the answer says whether it is to a whole-type request, the only kind that may mark a type
/// lane.
/// </summary>
public partial class RedeliveryPumpWindowTests {
  [Test]
  public async Task BeginAnswer_StampsOneAnswerIdOnEveryBundle_WithItsPlace_AndTheCountOnTheLastAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });
    var (a, b, c) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);
    var answer = pump.BeginAnswer("inbox.whizbang.service.billing", "billing", answersWholeType: true, TrackedGuid.New().Value);

    _ = await answer.PublishAsync([_evt(a, 1, 10), _evt(b, 1, 11)], new RedeliveryAnswerWindow(9, 20));
    _ = await answer.PublishAsync([_evt(c, 1, 12)], new RedeliveryAnswerWindow(9, 20));
    _ = await answer.CompleteAsync();

    var bundles = serializer.Composites;
    await Assert.That(bundles.Count).IsEqualTo(3);
    await Assert.That(bundles.Select(x => x.AnswerId).Distinct().Count()).IsEqualTo(1);
    await Assert.That(bundles[0].AnswerId.HasValue).IsTrue();
    await Assert.That(bundles[0].AnswerId).IsNotEqualTo(Guid.Empty);
    await Assert.That(string.Join(",", bundles.Select(x => x.AnswerBundleIndex))).IsEqualTo("0,1,2")
      .Because("each bundle's place is the order the origin published it in, held-back bundles included");
    await Assert.That(string.Join(",", bundles.Select(x => x.AnswerBundleCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"))).IsEqualTo("-,-,3");
    await Assert.That(bundles.All(x => x.AnswersWholeType)).IsTrue();
    await Assert.That(bundles.All(x => x.AnswerContinues == (x.AnswerBundleCount is null))).IsTrue()
      .Because("a receiver that predates the count still reads whether the answer continues");
    await Assert.That(string.Join(",", bundles.Select(x => x.InnerStreamIds![0]))).IsEqualTo($"{a},{b},{c}");
  }

  [Test]
  public async Task BeginAnswer_ForNamedStreams_SaysOnEveryBundleThatItIsNotAWholeTypeAnswerAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });
    var answer = pump.BeginAnswer("t", "billing", answersWholeType: false);

    _ = await answer.PublishAsync([_evt(TrackedGuid.New().Value, 1, 10), _evt(TrackedGuid.New().Value, 1, 11)],
      new RedeliveryAnswerWindow(null, null));
    _ = await answer.CompleteAsync();

    await Assert.That(serializer.Composites.Count).IsEqualTo(2);
    await Assert.That(serializer.Composites.Any(x => x.AnswersWholeType)).IsFalse();
    await Assert.That(serializer.Composites.All(x => x.AnswerId is not null)).IsTrue();
  }

  [Test]
  public async Task PublishAnswer_IsOneAnswer_AndTwoPublishesAreTwoAnswersAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 1 });

    _ = await pump.PublishAnswerAsync([_evt(TrackedGuid.New().Value, 1, 10), _evt(TrackedGuid.New().Value, 1, 11)],
      new RedeliveryAnswerWindow(9, 20), topic: "t", target: "billing", answersWholeType: true);
    _ = await pump.PublishAnswerAsync([_evt(TrackedGuid.New().Value, 1, 12)],
      new RedeliveryAnswerWindow(9, 20), topic: "t", target: "billing");

    var bundles = serializer.Composites;
    await Assert.That(bundles[0].AnswerId).IsEqualTo(bundles[1].AnswerId);
    await Assert.That(bundles[2].AnswerId).IsNotEqualTo(bundles[0].AnswerId);
    await Assert.That(string.Join(",", bundles.Select(x => $"{x.AnswerBundleIndex}/{x.AnswerBundleCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}/{x.AnswersWholeType}")))
      .IsEqualTo("0/-/True,1/2/True,0/1/False")
      .Because("an answer is whole-type only when its caller says so; the default marks no type lane");
  }

  [Test]
  public async Task Publish_ThatAnswersNoRequest_NamesNoAnswerAsync() {
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(new CaptureTransport(), serializer, _instance(), new CompositeFactory());

    _ = await pump.PublishAsync([_evt(TrackedGuid.New().Value, 1, 10)], topic: "t", target: "billing");

    await Assert.That(serializer.Composites.Single().AnswerId).IsNull();
    await Assert.That(((IIdentityPreservingComposite)serializer.Composites.Single()).AnsweredWindow).IsNull();
  }

  [Test]
  public async Task AnsweredWindow_CarriesTheBundlesPlaceInItsAnswer_OrNoneFromAnOlderOriginAsync() {
    var answerId = TrackedGuid.New().Value;
    IIdentityPreservingComposite stamped = new RedeliveryComposite {
      AnswersWindow = true,
      AnswerId = answerId,
      AnswerBundleIndex = 2,
      AnswerBundleCount = 3,
      AnswersWholeType = true,
    };
    IIdentityPreservingComposite older = new RedeliveryComposite { AnswersWindow = true };

    await Assert.That(stamped.AnsweredWindow!.Value.Part).IsEqualTo(new RedeliveryAnswerPart(answerId, 2, 3, WholeType: true));
    await Assert.That(older.AnsweredWindow!.Value.Part).IsNull()
      .Because("a bundle from an origin that predates the field is judged one bundle at a time, as before");
  }
}
