// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A selected-streams rebuild that names no streams must rebuild nothing. It must never widen to
/// the whole perspective, which is the largest operation the command can perform: a full replay
/// and, where a swapper is configured, a table swap.
/// </summary>
/// <remarks>
/// The assertion is on the outcome rather than on how the refusal is reported, so it holds whatever
/// shape the refusal takes. "Rebuild nothing" and "refuse loudly" are separable; this test pins the
/// one that protects data.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RebuildPerspectiveCommandReceptor.cs</code-under-test>
[Category("Shard1")]
public class SelectedStreamsWithoutStreamIdsTests {

  [Test]
  public async Task HandleAsync_SelectedStreamsNamingNoStreams_RebuildsNothingAsync() {
    var empty = await _handleAsync(new RebuildPerspectiveCommand(
      PerspectiveNames: [_perspectiveName],
      Mode: RebuildMode.SelectedStreams,
      IncludeStreamIds: []));

    var nullFilter = await _handleAsync(new RebuildPerspectiveCommand(
      PerspectiveNames: [_perspectiveName],
      Mode: RebuildMode.SelectedStreams,
      IncludeStreamIds: null));

    await Assert.That(empty.Calls).IsEmpty()
      .Because("an empty stream list means no streams were asked for, so nothing may be rebuilt; "
             + "widening it to every stream turns a targeted repair into a full re-derivation");
    await Assert.That(nullFilter.Calls).IsEmpty()
      .Because("a null stream list is the same request as an empty one and takes the same answer");
  }

  private const string _perspectiveName = "Consumer.Perspectives.Example+Projection";

  private static async Task<RecordingRebuilder> _handleAsync(RebuildPerspectiveCommand command) {
    var rebuilder = new RecordingRebuilder();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new OnePerspectiveRegistry(_perspectiveName));
    await using var sp = services.BuildServiceProvider();

    var receptor = new RebuildPerspectiveCommandReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RebuildPerspectiveCommandReceptor>.Instance);
    await receptor.HandleAsync(command, CancellationToken.None);
    return rebuilder;
  }

  /// <summary>Records which rebuild the receptor chose, so the test can assert it chose none.</summary>
  private sealed class RecordingRebuilder : IPerspectiveRebuilder {
    public List<string> Calls { get; } = [];

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
      Calls.Add("BlueGreen");
      return Task.FromResult(_none(perspectiveName));
    }

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) {
      Calls.Add("InPlace");
      return Task.FromResult(_none(perspectiveName));
    }

    public Task<RebuildResult> RebuildStreamsAsync(
        string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
      Calls.Add("Streams");
      return Task.FromResult(_none(perspectiveName));
    }

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);

    private static RebuildResult _none(string perspectiveName) =>
      new(perspectiveName, 0, 0, TimeSpan.Zero, Success: true, Error: null);
  }

  /// <summary>A registry hosting exactly one perspective, so the receptor has something to select.</summary>
  private sealed class OnePerspectiveRegistry(string clrTypeName) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new(clrTypeName, $"global::{clrTypeName}", "global::Consumer.Models.ExampleModel", [])];

    public IReadOnlyList<Type> GetEventTypes() => [];

    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
  }
}
