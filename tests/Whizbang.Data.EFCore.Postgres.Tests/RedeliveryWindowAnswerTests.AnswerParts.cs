// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2340, #2341: a backfill window's pages are one answer, each bundle naming it and its place in it, and a window that
/// names no streams answers its whole type.
/// </summary>
public partial class RedeliveryWindowAnswerTests {
  [Test]
  public async Task Window_OverSeveralPages_IsOneWholeTypeAnswer_CountedOnItsLastBundleAsync() {
    var coordinator = new WindowCoordinator {
      Events = [_evt(_s1, 1, 1), _evt(_s2, 1, 2), _evt(_s3, 1, 3), _evt(_s1, 2, 4), _evt(_s2, 2, 5)],
    };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer, new RedeliveryPumpOptions { SelectPageSize = 2 });

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 2, from: 0, max: 100));

    var bundles = serializer.Composites;
    await Assert.That(bundles.Count).IsEqualTo(3).Because("precondition: three pages, one bundle each");
    await Assert.That(bundles.Select(b => b.AnswerId).Distinct().Count()).IsEqualTo(1);
    await Assert.That(bundles[0].AnswerId.HasValue).IsTrue();
    await Assert.That(string.Join(",", bundles.Select(b => $"{b.AnswerBundleIndex}/{b.AnswerBundleCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}")))
      .IsEqualTo("0/-,1/-,2/3");
    await Assert.That(bundles.All(b => b.AnswersWholeType)).IsTrue()
      .Because("a window that names no streams walks the whole of its type");
  }

  [Test]
  public async Task Window_ThatNamesStreams_IsNotAWholeTypeAnswerAsync() {
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1)] };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer);

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 1, from: 0, max: 100) with { StreamIds = [_s1] });

    await Assert.That(serializer.Composites.Count).IsEqualTo(1);
    await Assert.That(serializer.Composites[0].AnswersWholeType).IsFalse();
  }
}
