// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.MultiStream;

/// <summary>
/// Where a rebuild command's work runs (#1357, #1353). With a multi-stream store, a rebuild of named streams, or an
/// in-place rebuild, is recorded as a run and each stream is rebuilt by its owner; the instance that received the command
/// rebuilds nothing itself, so the rebuild is neither racing the owners' live work nor bounded by the inbox handler's
/// budget. A blue-green rebuild stays one coordinated job, handed to the background rebuild queue so the handler
/// returns at once. Without a store or a queue, the rebuild runs where it was received, as before. No database.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RebuildPerspectiveCommandReceptor.cs</code-under-test>
[Category("Shard1")]
public class RebuildCommandRoutingTests {
  private const string PERSPECTIVE = "Test.Balance";
  private const string OTHER = "Test.Inventory";
  private static readonly Guid _a = Guid.Parse("019a0000-0000-7000-8000-0000000000a1");
  private static readonly Guid _b = Guid.Parse("019a0000-0000-7000-8000-0000000000b2");

  private static (RebuildPerspectiveCommandReceptor Receptor, FakeRebuilder Rebuilder, CapturingStore Store) _receptor(
      bool withStore = true, IReadOnlyList<Guid>? resolvable = null, RebuildBackgroundQueue? queue = null, params string[] perspectives) {
    var rebuilder = new FakeRebuilder { Resolvable = resolvable };
    var store = new CapturingStore();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakeRunnerRegistry(perspectives.Length == 0 ? [PERSPECTIVE] : perspectives));
    if (withStore) {
      services.AddSingleton<IMultiStreamCommandStore>(store);
    }
    if (queue is not null) {
      services.AddSingleton(queue);
    }
    var sp = services.BuildServiceProvider();
    return (new RebuildPerspectiveCommandReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RebuildPerspectiveCommandReceptor>.Instance),
      rebuilder, store);
  }

  [Test]
  public async Task NamedStreams_WithAStore_AreRecordedAsARunAndRebuiltNothingHereAsync() {
    var (receptor, rebuilder, store) = _receptor();

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace, IncludeStreamIds: [_a, _b]));

    var run = store.Recorded.Single();
    await Assert.That(run.StreamIds).IsEquivalentTo([_a, _b]);
    await Assert.That(run.CommandType).IsEqualTo(Whizbang.Core.TypeNameFormatter.Format(typeof(RebuildPerspectiveStreamsCommand)));
    await Assert.That(rebuilder.Rebuilds).IsEmpty()
      .Because("the receiving instance does not own these streams; each owner rebuilds its own under its own lease");
  }

  [Test]
  public async Task NamedStreams_ForTwoPerspectives_RecordOneRunEachAsync() {
    var (receptor, rebuilder, store) = _receptor(true, null, null, PERSPECTIVE, OTHER);

    await receptor.HandleAsync(new RebuildPerspectiveCommand(null, RebuildMode.InPlace, IncludeStreamIds: [_a]));

    await Assert.That(store.Recorded.Count).IsEqualTo(2);
    await Assert.That(store.Recorded.Select(r => r.RunId).Distinct().Count()).IsEqualTo(2);
    await Assert.That(rebuilder.Rebuilds).IsEmpty();
  }

  [Test]
  public async Task InPlace_WithAStore_RecordsEveryStreamTheRebuilderResolvesAsync() {
    var (receptor, rebuilder, store) = _receptor(resolvable: [_a, _b]);

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace));

    await Assert.That(store.Recorded.Single().StreamIds).IsEquivalentTo([_a, _b]);
    await Assert.That(rebuilder.Rebuilds).IsEmpty();
  }

  [Test]
  public async Task InPlace_WhenTheRebuilderCannotListTheStreams_RebuildsHereAsBeforeAsync() {
    var (receptor, rebuilder, store) = _receptor(resolvable: null);

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace));

    await Assert.That(store.Recorded).IsEmpty();
    await Assert.That(rebuilder.Rebuilds.Single()).IsEqualTo("InPlace:" + PERSPECTIVE);
  }

  [Test]
  public async Task InPlace_WithNoStreamsToRebuild_RecordsNothingAndRebuildsNothingAsync() {
    var (receptor, rebuilder, store) = _receptor(resolvable: []);

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace));

    await Assert.That(store.Recorded).IsEmpty().Because("an empty list is nothing to do, never every stream");
    await Assert.That(rebuilder.Rebuilds).IsEmpty();
  }

  [Test]
  public async Task TheSameRequestTwice_RecordsTheSameRunIdAsync() {
    var (receptor, _, store) = _receptor();
    var request = Guid.Parse("019a0000-0000-7000-8000-0000000000ff");
    var command = new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace, IncludeStreamIds: [_a], RequestId: request);

    await receptor.HandleAsync(command);
    await receptor.HandleAsync(command);

    await Assert.That(store.Recorded[0].RunId).IsEqualTo(store.Recorded[1].RunId)
      .Because("a redelivered rebuild command is one run, not a second rebuild of every stream");
  }

  [Test]
  public async Task WithoutARequestId_EachCommandIsItsOwnRunAsync() {
    var (receptor, _, store) = _receptor();
    var command = new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace, IncludeStreamIds: [_a]);

    await receptor.HandleAsync(command);
    await receptor.HandleAsync(command);

    await Assert.That(store.Recorded[0].RunId).IsNotEqualTo(store.Recorded[1].RunId);
  }

  [Test]
  public async Task NamedStreams_WithoutAStore_RebuildHereAsBeforeAsync() {
    var (receptor, rebuilder, _) = _receptor(withStore: false);

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace, IncludeStreamIds: [_a]));

    await Assert.That(rebuilder.Rebuilds.Single()).IsEqualTo("Streams:" + PERSPECTIVE);
  }

  [Test]
  public async Task NamedStreams_WithTheNullStore_RebuildHereAsBeforeAsync() {
    var rebuilder = new FakeRebuilder();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakeRunnerRegistry(PERSPECTIVE));
    services.AddSingleton<IMultiStreamCommandStore>(NullMultiStreamCommandStore.Instance);
    var sp = services.BuildServiceProvider();
    var receptor = new RebuildPerspectiveCommandReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RebuildPerspectiveCommandReceptor>.Instance);

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.InPlace, IncludeStreamIds: [_a]));

    await Assert.That(rebuilder.Rebuilds.Single()).IsEqualTo("Streams:" + PERSPECTIVE);
  }

  [Test]
  public async Task BlueGreen_WithAQueue_IsQueuedAndTheHandlerDoesNotRunItAsync() {
    var queue = new RebuildBackgroundQueue();
    var (receptor, rebuilder, store) = _receptor(queue: queue);

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.BlueGreen));

    await Assert.That(rebuilder.Rebuilds).IsEmpty()
      .Because("a whole-perspective rebuild runs off the inbox handler's budget, so the handler only queues it");
    await Assert.That(queue.Pending).IsEqualTo(1);
    await Assert.That(store.Recorded).IsEmpty().Because("blue-green builds and swaps one table, so it stays one coordinated job");
  }

  [Test]
  public async Task BlueGreen_WithoutAQueue_RunsHereAsBeforeAsync() {
    var (receptor, rebuilder, _) = _receptor();

    await receptor.HandleAsync(new RebuildPerspectiveCommand([PERSPECTIVE], RebuildMode.BlueGreen));

    await Assert.That(rebuilder.Rebuilds.Single()).IsEqualTo("BlueGreen:" + PERSPECTIVE);
  }

  internal sealed class FakeRebuilder : IPerspectiveRebuilder {
    public List<string> Rebuilds { get; } = [];

    public IReadOnlyList<Guid>? Resolvable { get; init; }

    public Task<IReadOnlyList<Guid>?> ResolveStreamIdsAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Resolvable);

    private Task<RebuildResult> _record(string what, string perspectiveName) {
      Rebuilds.Add(what + ":" + perspectiveName);
      return Task.FromResult(new RebuildResult(perspectiveName, 1, 1, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) =>
      _record("Streams", perspectiveName);
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, RebuildOrigin origin, CancellationToken ct = default) =>
      _record("Streams", perspectiveName);
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => _record("BlueGreen", perspectiveName);
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) =>
      _record("BlueGreen", perspectiveName);
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => _record("InPlace", perspectiveName);
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) =>
      _record("InPlace", perspectiveName);
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
  }

  private sealed class FakeRunnerRegistry(params string[] perspectiveNames) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => throw new NotSupportedException();
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [.. perspectiveNames.Select(n => new PerspectiveRegistrationInfo(n, $"global::{n}", "global::Test.Model", []))];
    public IReadOnlySet<Whizbang.Core.Messaging.LifecycleStage> LifecycleStagesWithReceptors => new HashSet<Whizbang.Core.Messaging.LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class CapturingStore : IMultiStreamCommandStore {
    public List<MultiStreamRun> Recorded { get; } = [];

    public Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken) {
      Recorded.Add(run);
      return Task.FromResult(true);
    }

    public Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
        Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, TimeSpan releaseDelay, CancellationToken cancellationToken) =>
      throw new NotSupportedException();
    public Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
  }
}
