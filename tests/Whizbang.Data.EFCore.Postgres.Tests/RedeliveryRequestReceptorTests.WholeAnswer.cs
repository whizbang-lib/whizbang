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
/// #2311: an ordinary answer selected in pages is one answer. Every bundle says more of it follows except the very last,
/// including when the last page is full and only the next, empty, selection shows the answer ended there.
/// </summary>
public partial class RedeliveryRequestReceptorTests {
  [Test]
  public async Task Receptor_AnAnswerOverSeveralPages_EndsOnlyWithItsLastBundleAsync() {
    var serializer = await _answerAsync(new RedeliveryPumpOptions { SelectPageSize = 2, MaxEventsPerRequest = 100 },
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_first, TrackedGuid.New().Value, 2), _evt(_first, TrackedGuid.New().Value, 3),
      _evt(_second, TrackedGuid.New().Value, 1), _evt(_second, TrackedGuid.New().Value, 2));

    var continues = serializer.Captured.ConvertAll(c => c.Payload.AnswerContinues);
    await Assert.That(string.Join(",", continues)).IsEqualTo("True,True,True,False")
      .Because("a bundle that changed nothing must not let the receiver mark the type lane while the answer goes on");
  }

  [Test]
  public async Task Receptor_AnAnswerWhoseLastPageIsFull_StillEndsWithItsLastBundleAsync() {
    var serializer = await _answerAsync(new RedeliveryPumpOptions { SelectPageSize = 2, MaxEventsPerRequest = 100 },
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_second, TrackedGuid.New().Value, 1));

    var bundles = serializer.Captured.ConvertAll(c => c.Payload);
    await Assert.That(bundles.Count).IsEqualTo(2).Because("precondition: one full page, one bundle per stream");
    await Assert.That(string.Join(",", bundles.Select(b => b.AnswerContinues))).IsEqualTo("True,False")
      .Because("the empty selection after a full page ends the answer, and its held-back last bundle then says so");
    await Assert.That(string.Join(",", bundles.Select(b => b.InnerStreamIds![0]))).IsEqualTo($"{_first},{_second}")
      .Because("holding the last bundle back does not reorder the answer");
  }

  [Test]
  public async Task Receptor_AnAnswerThatSelectsNothing_PublishesNoBundleAsync() {
    var serializer = await _answerAsync(new RedeliveryPumpOptions { SelectPageSize = 2, MaxEventsPerRequest = 100 });

    await Assert.That(serializer.Captured).IsEmpty();
  }
}
