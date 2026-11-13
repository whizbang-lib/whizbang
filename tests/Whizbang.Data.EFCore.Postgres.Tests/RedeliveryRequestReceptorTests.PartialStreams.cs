// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2149: an ordinary answer selects in (stream, version) pages up to the origin's cap, and every bundle claims the
/// request's window. A stream whose events run past a page, or stop at the cap, is carried by a bundle only in part, so
/// the bundle names it and the receiver does not mark its bucket from that bundle.
/// </summary>
public partial class RedeliveryRequestReceptorTests {
  private static readonly Guid _first = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _second = Guid.Parse("22222222-2222-2222-2222-222222222222");

  private static async Task<CaptureSerializer> _answerAsync(RedeliveryPumpOptions options, params RedeliveryEvent[] selection) {
    var coordinator = new SelectingCoordinator { Selection = selection };
    var serializer = new CaptureSerializer();
    await using var sp = _buildProvider(coordinator, new CaptureTransport(), serializer, options);
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);
    await receptor.HandleAsync(new RequestRedeliveryCommand { RequesterService = "svc", Topic = "t", FromCommitSequence = 0 });
    return serializer;
  }

  [Test]
  public async Task Receptor_AStreamThatRunsAcrossPages_IsNamedPartialOnEveryPageItIsOnAsync() {
    var serializer = await _answerAsync(new RedeliveryPumpOptions { SelectPageSize = 2, MaxEventsPerRequest = 100 },
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_first, TrackedGuid.New().Value, 2), _evt(_first, TrackedGuid.New().Value, 3),
      _evt(_second, TrackedGuid.New().Value, 1), _evt(_second, TrackedGuid.New().Value, 2));

    var bundles = serializer.Captured.Select(c => c.Payload).ToList();
    await Assert.That(bundles.Count).IsEqualTo(4).Because("precondition: pages [1a 1b] [1c 2a] [2b], one bundle per stream per page");
    foreach (var bundle in bundles) {
      await Assert.That(bundle.AnsweredPartialStreamIds!).IsEquivalentTo([bundle.InnerStreamIds![0]])
        .Because("every bundle here holds a stream whose other events are on the page before or after it");
    }
  }

  [Test]
  public async Task Receptor_StreamsThatEndInsideTheLastPage_AreNotNamedPartialAsync() {
    var serializer = await _answerAsync(new RedeliveryPumpOptions { SelectPageSize = 3, MaxEventsPerRequest = 100 },
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_second, TrackedGuid.New().Value, 1));

    await Assert.That(serializer.Captured.All(c => c.Payload.AnsweredPartialStreamIds is null)).IsTrue()
      .Because("a page shorter than asked for is the end of the selection: each stream on it is carried whole");
  }

  [Test]
  public async Task Receptor_AStreamCutAtTheOriginsCap_IsNamedPartialAsync() {
    var serializer = await _answerAsync(new RedeliveryPumpOptions { SelectPageSize = 100, MaxEventsPerRequest = 2 },
      _evt(_first, TrackedGuid.New().Value, 1), _evt(_first, TrackedGuid.New().Value, 2), _evt(_first, TrackedGuid.New().Value, 3));

    var bundle = serializer.Captured.Single().Payload;
    await Assert.That(bundle.AnsweredPartialStreamIds!).IsEquivalentTo([_first])
      .Because("the stream's third event lies beyond the cap, so a bundle that changed nothing has not shown it is unhealable");
  }
}
