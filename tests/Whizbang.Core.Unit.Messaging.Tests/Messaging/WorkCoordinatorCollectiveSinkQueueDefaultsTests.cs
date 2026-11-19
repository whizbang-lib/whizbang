// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The default bodies of the collective sink queue reads (#1524). A coordinator that predates the bounded reads
/// keeps working: it provides no head, so the sink reads its whole queue as before, and the membership read filters
/// that whole queue.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
public class WorkCoordinatorCollectiveSinkQueueDefaultsTests {
  private static readonly Guid _stream = Guid.Parse("00000000-0000-7000-8000-000000000001");
  private static readonly CollectiveSinkQueueEntry _a = new(Guid.Parse("00000000-0000-7000-8000-00000000000a"), Guid.Parse("00000000-0000-7000-8000-0000000000ea"), 1);
  private static readonly CollectiveSinkQueueEntry _b = new(Guid.Parse("00000000-0000-7000-8000-00000000000b"), Guid.Parse("00000000-0000-7000-8000-0000000000eb"), 2);
  private static readonly CollectiveSinkQueueEntry _c = new(Guid.Parse("00000000-0000-7000-8000-00000000000c"), Guid.Parse("00000000-0000-7000-8000-0000000000ec"), null);

  [Test]
  public async Task Head_ByDefault_IsNotProvidedAsync() {
    IWorkCoordinator coordinator = new QueueOnlyCoordinator([_a, _b]);

    var head = await coordinator.FetchCollectiveSinkQueueHeadAsync(_stream, [_a.EventWorkId]);

    await Assert.That(head).IsNull()
      .Because("Null tells the sink to read the whole queue, which is all an older coordinator can give it.");
  }

  [Test]
  public async Task Members_ByDefault_FilterTheWholeQueueAsync() {
    IWorkCoordinator coordinator = new QueueOnlyCoordinator([_a, _b, _c]);

    var members = await coordinator.FetchCollectiveSinkQueueMembersAsync(
      _stream, workIds: [_a.EventWorkId, Guid.CreateVersion7()], eventIds: [_c.EventId]);

    await Assert.That(members).IsEquivalentTo([_a, _c])
      .Because("A row is a member when its work id or its event is named, and an unknown id names nothing.");
  }

  [Test]
  public async Task Members_ByDefault_WithoutAQueue_AreEmptyAsync() {
    IWorkCoordinator coordinator = new QueueOnlyCoordinator(null);

    var members = await coordinator.FetchCollectiveSinkQueueMembersAsync(_stream, [_a.EventWorkId], [_a.EventId]);

    await Assert.That(members).IsEmpty();
  }

  /// <summary>
  /// Implements the members <see cref="IWorkCoordinator"/> leaves abstract, and the whole-queue read; every other
  /// member resolves to the interface's own body.
  /// </summary>
  private sealed class QueueOnlyCoordinator(IReadOnlyList<CollectiveSinkQueueEntry>? queue) : IWorkCoordinator {
    public Task<IReadOnlyList<CollectiveSinkQueueEntry>?> FetchCollectiveSinkQueueAsync(
        Guid streamId, CancellationToken cancellationToken = default)
      => Task.FromResult(queue);

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
