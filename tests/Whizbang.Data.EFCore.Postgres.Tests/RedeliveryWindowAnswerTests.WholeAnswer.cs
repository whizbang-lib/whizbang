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
/// #2311: a backfill window's pages are one answer, so only its very last bundle says the answer ends there, and it is
/// sent before the window's answer.
/// </summary>
public partial class RedeliveryWindowAnswerTests {
  [Test]
  public async Task Window_OverSeveralPages_OnlyItsLastBundleEndsTheAnswerAsync() {
    var coordinator = new WindowCoordinator {
      Events = [_evt(_s1, 1, 1), _evt(_s2, 1, 2), _evt(_s3, 1, 3), _evt(_s1, 2, 4), _evt(_s2, 2, 5)],
    };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer, new RedeliveryPumpOptions { SelectPageSize = 2 });

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 2, from: 0, max: 100));

    await Assert.That(serializer.Composites.Count).IsEqualTo(3).Because("precondition: three pages, one bundle each");
    await Assert.That(string.Join(",", serializer.Composites.Select(c => c.AnswerContinues))).IsEqualTo("True,True,False")
      .Because("the receiver judges the window's answer as a whole, so only its last bundle may end it");
  }
}
