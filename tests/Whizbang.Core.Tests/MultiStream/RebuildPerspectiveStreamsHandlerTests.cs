// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>
/// A rebuild of named streams, worked per stream on each stream's owner (#1357): the handler rebuilds exactly the one
/// stream it is given, under the rebuild's origin, and a rebuild that did not succeed is a failed attempt, not a success.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
[Category("MultiStream")]
public class RebuildPerspectiveStreamsHandlerTests {
  private static readonly Guid _run = Guid.Parse("0199f000-0000-7000-8000-000000000001");
  private static readonly Guid _stream = Guid.Parse("0199f000-0000-7000-8000-0000000000a1");
  private static readonly RebuildOrigin _origin = new(Guid.Parse("0199f000-0000-7000-8000-0000000000ee"), RebuildTrigger.Requested, "operator-1");

  [Test]
  public async Task HandleStreamAsync_RebuildsThatOneStreamUnderTheRebuildsOriginAsync() {
    var rebuilder = new StreamRecordingRebuilder();
    var handler = new RebuildPerspectiveStreamsHandler(rebuilder);

    await handler.HandleStreamAsync(new RebuildPerspectiveStreamsCommand(_run, [_stream, Guid.CreateVersion7()], "Test.Perspective", _origin), _stream, CancellationToken.None);

    await Assert.That(rebuilder.RunCalls.Single()).IsEqualTo(("Test.Perspective", _stream, _origin))
      .Because("each stream is rebuilt on its own owner, so one call rebuilds one stream, never the whole list");
    await Assert.That(rebuilder.Calls).IsEmpty()
      .Because("a stream of a run is rebuilt as part of the run, which reports once, not as a rebuild of its own with its own events and digests");
  }

  [Test]
  public async Task HandleStreamAsync_ARebuildThatDidNotSucceed_ThrowsWithItsErrorAsync() {
    var handler = new RebuildPerspectiveStreamsHandler(new StreamRecordingRebuilder { FailWith = "no runner" });

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await handler.HandleStreamAsync(new RebuildPerspectiveStreamsCommand(_run, [_stream], "Test.Perspective", _origin), _stream, CancellationToken.None));

    await Assert.That(thrown!.Message).Contains("no runner")
      .Because("the stream's outcome must say it failed, and why, so the run's status is not a quiet success");
  }

  [Test]
  public async Task Constructor_NoRebuilder_ThrowsAsync() {
    await Assert.That(() => new RebuildPerspectiveStreamsHandler(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ResolveStreamIdsAsync_ARebuilderThatCannotList_SaysNothingAsync() {
    IPerspectiveRebuilder rebuilder = new StreamRecordingRebuilder();

    await Assert.That(await rebuilder.ResolveStreamIdsAsync("Test.Perspective", CancellationToken.None)).IsNull()
      .Because("a rebuilder that cannot list a perspective's streams says so, and the rebuild then runs where it was received");
  }

  /// <summary>
  /// A rebuilder written before runs rebuilt their streams quietly still works one: the default forwards to its own
  /// rebuild of that one stream, under the run's origin.
  /// </summary>
  [Test]
  public async Task RebuildStreamOfRunAsync_OnARebuilderThatPredatesIt_RebuildsThatOneStreamAsync() {
    var rebuilder = new PredatingRebuilder();

    var result = await ((IPerspectiveRebuilder)rebuilder).RebuildStreamOfRunAsync("Test.Perspective", _stream, _origin, CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(rebuilder.Calls.Single()).IsEqualTo(("Test.Perspective", _stream, _origin));
  }

  /// <summary>Implements only what the interface required before <c>RebuildStreamOfRunAsync</c>.</summary>
  private sealed class PredatingRebuilder : IPerspectiveRebuilder {
    public List<(string Perspective, Guid Stream, RebuildOrigin Origin)> Calls { get; } = [];

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, RebuildOrigin origin, CancellationToken ct = default) {
      foreach (var stream in streamIds) {
        Calls.Add((perspectiveName, stream, origin));
      }
      return Task.FromResult(new RebuildResult(perspectiveName, 1, 1, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
  }

  private sealed class StreamRecordingRebuilder : IPerspectiveRebuilder {
    public List<(string Perspective, Guid Stream, RebuildOrigin Origin)> Calls { get; } = [];

    public List<(string Perspective, Guid Stream, RebuildOrigin Origin)> RunCalls { get; } = [];

    public string? FailWith { get; init; }

    public Task<RebuildResult> RebuildStreamOfRunAsync(string perspectiveName, Guid streamId, RebuildOrigin origin, CancellationToken ct = default) {
      RunCalls.Add((perspectiveName, streamId, origin));
      return Task.FromResult(FailWith is null
        ? new RebuildResult(perspectiveName, 1, 1, TimeSpan.Zero, Success: true, Error: null)
        : new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, Success: false, Error: FailWith));
    }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, RebuildOrigin origin, CancellationToken ct = default) {
      foreach (var stream in streamIds) {
        Calls.Add((perspectiveName, stream, origin));
      }
      return Task.FromResult(FailWith is null
        ? new RebuildResult(perspectiveName, 1, 1, TimeSpan.Zero, Success: true, Error: null)
        : new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, Success: false, Error: FailWith));
    }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) =>
      throw new NotSupportedException("The handler always passes the origin.");
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
  }
}
