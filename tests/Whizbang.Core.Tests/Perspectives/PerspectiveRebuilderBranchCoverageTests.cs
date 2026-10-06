// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Branch backfill for <see cref="PerspectiveRebuilder"/>: the stream-group lookup skipping a
/// registered model type that has no CLR full name, and both arms of the internal
/// <c>QueryableExtensions.ToListAsync</c> (async-enumerable source vs plain queryable).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderBranchCoverageTests {
  private static readonly int[] _plainValues = [4, 5];

  private sealed class GroupAnnouncerModel;
  private sealed class GroupFollowerModel;

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_RegistryHoldsATypeWithNoFullName_StillFindsTheFollowerAndReconcilesAsync() {
    PerspectiveStreamGroupRegistry.Clear();
    try {
      // A generic type parameter is a Type whose FullName is null: the lookup must skip it rather
      // than match on, or throw over, a missing name.
      var namelessType = typeof(List<>).GetGenericArguments()[0];
      PerspectiveStreamGroupRegistry.Register(namelessType, "unrelated", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(typeof(GroupAnnouncerModel), "g", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(typeof(GroupFollowerModel), "g", announce: false, follow: true, bridge: false);

      var coordinator = new ReconcileCoordinator {
        Tables = {
          new(typeof(GroupFollowerModel).FullName!, "wh_per_follower"),
          new(typeof(GroupAnnouncerModel).FullName!, "wh_per_announcer"),
        },
      };
      var services = new ServiceCollection();
      services.AddSingleton<IPerspectiveRunnerRegistry>(new FakePerspectiveRunnerRegistry(new FakePerspectiveRunner()));
      services.AddSingleton<IWorkCoordinator>(coordinator);
      await using var sp = services.BuildServiceProvider();
      var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);

      var result = await rebuilder.RebuildStreamsAsync(typeof(GroupFollowerModel).FullName!, [Guid.NewGuid()]);

      await Assert.That(namelessType.FullName).IsNull()
        .Because("precondition: the registered type really has no full name");
      await Assert.That(result.Success).IsTrue();
      await Assert.That(coordinator.Reconciled.Count).IsEqualTo(1)
        .Because("the nameless registration is skipped and the follower is still found and reconciled");
      await Assert.That(coordinator.Reconciled[0].Follower).IsEqualTo("wh_per_follower");
      await Assert.That(coordinator.Reconciled[0].Announcers).Contains("wh_per_announcer");
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  [Test]
  public async Task ToListAsync_AsyncEnumerableSource_ReadsThroughTheAsyncEnumeratorAsync() {
    var source = new AsyncOnlyQueryable([1, 2, 3]);

    // Called statically: the source is both IQueryable and IAsyncEnumerable, so the extension form
    // is ambiguous with System.Linq.AsyncEnumerable.ToListAsync.
    var list = await QueryableExtensions.ToListAsync(source, CancellationToken.None);

    await Assert.That(list).IsEquivalentTo([1, 2, 3]);
    await Assert.That(source.AsyncEnumerations).IsEqualTo(1)
      .Because("a source that is also IAsyncEnumerable must be drained asynchronously, never synchronously");
  }

  [Test]
  public async Task ToListAsync_PlainQueryable_ReadsSynchronouslyAsync() {
    var source = _plainValues.AsQueryable();

    var list = await source.ToListAsync(CancellationToken.None);

    await Assert.That(list).IsEquivalentTo([4, 5]);
  }

  // ── fakes ────────────────────────────────────────────────────────────────

  /// <summary>Queryable whose synchronous enumeration throws, so only the async path can produce items.</summary>
  private sealed class AsyncOnlyQueryable(IReadOnlyList<int> items) : IQueryable<int>, IAsyncEnumerable<int> {
    private readonly IQueryable<int> _inner = items.AsQueryable();
    public int AsyncEnumerations { get; private set; }
    public Type ElementType => typeof(int);
    public Expression Expression => _inner.Expression;
    public IQueryProvider Provider => _inner.Provider;
    public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("synchronous enumeration is not supported");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
      AsyncEnumerations++;
      foreach (var item in items) {
        await Task.Yield();
        yield return item;
      }
    }
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class FakePerspectiveRunnerRegistry(IPerspectiveRunner? runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class ReconcileCoordinator : IWorkCoordinator {
    public List<PerspectiveTableName> Tables { get; } = [];
    public List<(string Follower, IReadOnlyCollection<string> Announcers)> Reconciled { get; } = [];

    public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
        IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PerspectiveTableName>>([.. Tables.Where(t => clrTypeNames.Contains(t.ClrTypeName))]);

    public Task<int> ReconcileFollowerPresenceAsync(
        string followerTable, IReadOnlyCollection<string> announcerTables, CancellationToken cancellationToken = default) {
      lock (Reconciled) { Reconciled.Add((followerTable, announcerTables)); }
      return Task.FromResult(0);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
