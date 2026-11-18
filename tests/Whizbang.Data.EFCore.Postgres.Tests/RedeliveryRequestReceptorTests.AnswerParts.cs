// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2340, #2341: every bundle of an ordinary answer names its answer, its place in it, and (on the last) how many bundles
/// the answer has, so the receiver can judge the answer once all of it is in, whatever order its bundles commit in. And
/// the answer says whether it is to a whole-type request: only such an answer may mark a type lane.
/// </summary>
public partial class RedeliveryRequestReceptorTests {
  [Test]
  public async Task Receptor_AWholeTypeRequest_StampsOneAnswerOnEveryBundle_CountedOnTheLastAsync() {
    var bundles = await _answerPartsAsync(_wholeType(),
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_first, TrackedGuid.New().Value, 2), _evt(_first, TrackedGuid.New().Value, 3),
      _evt(_second, TrackedGuid.New().Value, 1), _evt(_second, TrackedGuid.New().Value, 2));

    await Assert.That(bundles.Count).IsEqualTo(4).Because("precondition: pages [1a 1b] [1c 2a] [2b], one bundle per stream per page");
    await Assert.That(bundles.Select(b => b.AnswerId).Distinct().Count()).IsEqualTo(1)
      .Because("the pages of one request are one answer");
    await Assert.That(bundles[0].AnswerId.HasValue).IsTrue();
    await Assert.That(bundles[0].AnswerId).IsNotEqualTo(Guid.Empty);
    await Assert.That(string.Join(",", bundles.Select(b => b.AnswerBundleIndex))).IsEqualTo("0,1,2,3");
    await Assert.That(string.Join(",", bundles.Select(b => b.AnswerBundleCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"))).IsEqualTo("-,-,-,4")
      .Because("only the last bundle knows, and says, how many bundles the answer has");
    await Assert.That(bundles.All(b => b.AnswersWholeType)).IsTrue()
      .Because("the request named no streams and no events: the answer covers the whole of its type");
  }

  [Test]
  public async Task Receptor_ARequestForNamedStreams_SaysItsAnswerIsNotForTheWholeTypeAsync() {
    var bundles = await _answerPartsAsync(_wholeType() with { StreamIds = [_first, _second] },
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_second, TrackedGuid.New().Value, 1));

    await Assert.That(bundles.Count).IsGreaterThan(0);
    await Assert.That(bundles.Any(b => b.AnswersWholeType)).IsFalse()
      .Because("an answer for named streams says nothing about the type's other streams (#2341)");
    await Assert.That(bundles.All(b => b.AnswerId is not null)).IsTrue();
  }

  [Test]
  public async Task Receptor_ARequestForNamedEvents_SaysItsAnswerIsNotForTheWholeTypeAsync() {
    var named = TrackedGuid.New().Value;
    var bundles = await _answerPartsAsync(_wholeType() with { EventIds = [named] }, _evt(_first, named, 1));

    await Assert.That(bundles.Count).IsEqualTo(1);
    await Assert.That(bundles[0].AnswersWholeType).IsFalse();
  }

  [Test]
  public async Task Receptor_ARequestWithEmptyStreamAndEventLists_IsAWholeTypeRequestAsync() {
    var bundles = await _answerPartsAsync(_wholeType() with { StreamIds = [], EventIds = [] }, _evt(_first, TrackedGuid.New().Value, 1));

    await Assert.That(bundles.Count).IsEqualTo(1);
    await Assert.That(bundles[0].AnswersWholeType).IsTrue().Because("an empty filter names nothing, as an absent one does");
  }

  [Test]
  public async Task Receptor_TwoRequests_AreTwoAnswersAsync() {
    var first = await _answerPartsAsync(_wholeType(), _evt(_first, TrackedGuid.New().Value, 1));
    var second = await _answerPartsAsync(_wholeType(), _evt(_first, TrackedGuid.New().Value, 1));

    await Assert.That(first.Single().AnswerId).IsNotEqualTo(second.Single().AnswerId);
    await Assert.That((first.Single().AnswerBundleIndex, first.Single().AnswerBundleCount)).IsEqualTo((0, (int?)1));
  }

  private static RequestRedeliveryCommand _wholeType() => new() {
    RuleFingerprint = IntegrityPairGuard.Fallback.Stamp(null),
    EventTypes = ["Contracts.ProbeHappened"],
    RequesterService = "svc",
    Topic = "t",
    FromCommitSequence = 0,
  };

  private static async Task<List<RedeliveryComposite>> _answerPartsAsync(
      RequestRedeliveryCommand request, params RedeliveryEvent[] selection) {
    var coordinator = new SelectingCoordinator { Selection = selection };
    var serializer = new CaptureSerializer();
    await using var sp = _buildProvider(coordinator, new CaptureTransport(), serializer,
      new RedeliveryPumpOptions { SelectPageSize = 2, MaxEventsPerRequest = 100 });
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);
    await receptor.HandleAsync(request);
    return serializer.Captured.ConvertAll(c => c.Payload);
  }
}
