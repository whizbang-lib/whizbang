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
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2225: a stream-level walk keeps asking for every type it started with. Each follow-up page used to ask only for the
/// types that appeared on the page before it, so a type with no streams on one page dropped out of the rest of the walk,
/// even when later pages held its streams. The origin now echoes the types a stream-level ask named
/// (<see cref="IntegrityManifest.RequestedEventTypes"/>), and the follow-up asks for those.
/// </summary>
public partial class IntegrityManifestReceptorTests {
  [Test]
  public async Task RequestReceptor_AStreamLevelAnswer_EchoesTheTypesTheAskNamedAsync() {
    var (_, transport) = await _answerAsync(
      _windowedAsk(since: 400, until: 900, ManifestLevel.Streams) with { EventTypes = ["Contracts.TypeX", "Contracts.TypeY"] },
      computedThrough: 900);

    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.RequestedEventTypes).IsNotNull();
    await Assert.That(manifest.RequestedEventTypes).IsEquivalentTo(["Contracts.TypeX", "Contracts.TypeY"])
      .Because("the asker's next page must ask for every type of the walk, and only the answer can carry them to it");
  }

  [Test]
  public async Task RequestReceptor_ATypeLevelAnswer_EchoesNoTypesAsync() {
    var (_, transport) = await _answerAsync(_windowedAsk(since: 400, until: 900), computedThrough: 900);

    await Assert.That(_deserializeManifest(transport.Published.Single().Envelope).RequestedEventTypes).IsNull()
      .Because("a type-level answer is never paged by stream, so it has no walk to carry");
  }

  [Test]
  public async Task ManifestReceptor_AFollowUpPage_AsksForEveryTypeTheWalkStartedWith_NotOnlyThoseOnThePageAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 5);
    var stream = TrackedGuid.New().Value;
    var cursor = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 41, 42, 5)];

    // A page of a walk over TypeX and TypeY that happens to hold only TypeX streams.
    await receptor.HandleAsync(_streamPage(coordinator, stream, cursor) with { RequestedEventTypes = ["Contracts.TypeX", "Contracts.TypeY"] });

    var follow = _streamAsks(transport).Single();
    await Assert.That(follow.EventTypes).IsEquivalentTo(["Contracts.TypeX", "Contracts.TypeY"])
      .Because("TypeY's streams may sit on a later page; asking only for the types this page held drops it from the walk");
    await Assert.That(follow.ResumeAfterStreamId).IsEqualTo(cursor);
  }

  [Test]
  public async Task ManifestReceptor_AFollowUpPage_FromAnOriginThatEchoesNoTypes_AsksForThePagesTypesAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 5);
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 41, 42, 5)];

    await receptor.HandleAsync(_streamPage(coordinator, stream, TrackedGuid.New().Value));

    await Assert.That(_streamAsks(transport).Single().EventTypes).IsEquivalentTo(["Contracts.TypeX"])
      .Because("an origin that predates the echo says nothing about the walk, so the page's own types are all there is");
  }

  [Test]
  public async Task ManifestReceptor_AFollowUpPage_WithNoDigestOfItsOwn_StillAsksForTheWalksTypesAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 5);
    var cursor = TrackedGuid.New().Value;

    // A page whose every stream belonged to types the origin does not own: it arrives with no digests and a cursor.
    await receptor.HandleAsync(_manifest(coordinator, []) with {
      SinceSequence = CURSOR_SINCE,
      ComputedThrough = CURSOR_THROUGH,
      ChunkCount = 1,
      ResumeAfterStreamId = cursor,
      RequestedEventTypes = ["Contracts.TypeX"],
    });

    var follow = _streamAsks(transport).Single();
    await Assert.That(follow.EventTypes).IsEquivalentTo(["Contracts.TypeX"])
      .Because("an empty page names no type; asking for none would end the walk or ask for nothing");
  }
  [Test]
  public async Task ManifestReceptor_APageOfOnlyTypesTheOriginIsNotAuthoritativeFor_StillFollowsTheWalkAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 5);
    coordinator.Claims = [new IntegrityDomainClaim {
      Domain = "app.contracts.billing",
      OwnerServiceId = TrackedGuid.New().Value,
      OwnerServiceName = "billing-svc",
    }];
    var cursor = TrackedGuid.New().Value;
    var billing = _digest(TrackedGuid.New().Value, 4, 5, 6) with { EventType = "App.Contracts.Billing.Raised, App.Contracts" };

    await receptor.HandleAsync(_manifest(coordinator, [billing]) with {
      SinceSequence = CURSOR_SINCE,
      ComputedThrough = CURSOR_THROUGH,
      ChunkCount = 1,
      ResumeAfterStreamId = cursor,
      RequestedEventTypes = ["Contracts.TypeX"],
    });

    var follow = _streamAsks(transport).SingleOrDefault();
    await Assert.That(follow).IsNotNull()
      .Because("a page whose every digest is left out of the compare still says there is more; dropping it ends the walk");
    await Assert.That(follow!.EventTypes).IsEquivalentTo(["Contracts.TypeX"]);
    await Assert.That(follow.ResumeAfterStreamId).IsEqualTo(cursor);
  }
  [Test]
  public async Task ManifestReceptor_AnEmptyPage_FromAnOriginThatEchoesNoTypes_EndsTheWalkAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 5);

    await receptor.HandleAsync(_manifest(coordinator, []) with {
      SinceSequence = CURSOR_SINCE,
      ComputedThrough = CURSOR_THROUGH,
      ChunkCount = 1,
      ResumeAfterStreamId = TrackedGuid.New().Value,
    });

    await Assert.That(_streamAsks(transport)).IsEmpty()
      .Because("the page names no type and the origin echoed none, so a follow-up would ask for nothing");
  }
  [Test]
  public async Task ManifestReceptor_AnEmptyLastPage_EndsTheWalkAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 5);

    await receptor.HandleAsync(_manifest(coordinator, []) with {
      SinceSequence = CURSOR_SINCE,
      ComputedThrough = CURSOR_THROUGH,
      ChunkCount = 1,
      RequestedEventTypes = ["Contracts.TypeX"],
    });

    await Assert.That(_streamAsks(transport)).IsEmpty().Because("a page with no cursor is the walk's last");
  }
}
