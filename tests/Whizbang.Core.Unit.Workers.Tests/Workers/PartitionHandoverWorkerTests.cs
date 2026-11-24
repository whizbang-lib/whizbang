// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The old owner's side of a handover (#2557, design 3.4): for each partition it is handing over, once no stream of that
/// partition is still being worked, it saves what it holds there (write-behind models, through each participant), waits
/// for its queued writes to land, and acknowledges, which releases what it has not started and flips the partition to the
/// next owner. A partition whose work is still in flight waits for a later pass.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PartitionHandoverWorker.cs</code-under-test>
public sealed class PartitionHandoverWorkerTests {
  private static readonly Guid _self = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _next = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  public async Task NothingHandingOver_AcknowledgesNothingAsync() {
    var (worker, coordinator, _, _) = _create();

    var flipped = await worker.HandOverAsync(CancellationToken.None);

    await Assert.That(flipped).IsEmpty();
    await Assert.That(coordinator.Acknowledged).IsEmpty();
  }

  [Test]
  public async Task APartitionWithAStreamInFlight_Waits_AndTheOthersAreAcknowledgedAsync() {
    var (worker, coordinator, participant, tenures) = _create();
    var busy = (Guid)TrackedGuid.New();
    _claim(tenures, busy, partition: 3, epoch: 5);
    coordinator.HandingOver.AddRange([new PartitionHandover(3, 5, _next), new PartitionHandover(4, 7, _next)]);
    participant.InFlight.Add(busy);

    var first = await worker.HandOverAsync(CancellationToken.None);
    participant.InFlight.Clear();
    var second = await worker.HandOverAsync(CancellationToken.None);

    await Assert.That(first).IsEquivalentTo([4]).Because("a partition is handed over only once its in-flight batch is done");
    await Assert.That(second).IsEquivalentTo([3]);
    await Assert.That(tenures.OfStream(busy).HasValue).IsFalse()
      .Because("a handed-over partition's tenures are forgotten, so nothing later is judged by a tenure no longer held");
    await Assert.That(coordinator.Acknowledged.Select(a => (a.Partition, a.Epoch))).IsEquivalentTo([(4, 7L), (3, 5L)])
      .Because("each acknowledgement names the epoch it hands over at, so a stale one flips nothing");
  }

  [Test]
  public async Task HeldWorkOfAReadyPartition_IsSavedBeforeTheAcknowledgement_AndNoOtherAsync() {
    var (worker, coordinator, participant, tenures) = _create();
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    _claim(tenures, moved, partition: 3, epoch: 5);
    _claim(tenures, kept, partition: 9, epoch: 2);
    coordinator.HandingOver.Add(new PartitionHandover(3, 5, _next));
    var events = new List<string>();
    participant.OnSave = handsOver => events.Add($"save moved={handsOver(moved)} kept={handsOver(kept)}");
    coordinator.OnAcknowledge = () => events.Add("ack");

    _ = await worker.HandOverAsync(CancellationToken.None);

    await Assert.That(events).IsEquivalentTo(["save moved=True kept=False", "ack"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a clean handover saves what it holds for the moved partition, then acknowledges (design 3.4 steps 4-6)");
  }

  [Test]
  public async Task TheQueuedWritesLand_BeforeTheAcknowledgementAsync() {
    var landed = new GatedLanded();
    var (worker, coordinator, _, _) = _create(landed);
    coordinator.HandingOver.Add(new PartitionHandover(3, 5, _next));

    var pass = worker.HandOverAsync(CancellationToken.None);
    await landed.Waiting;
    var acknowledgedBeforeLanding = coordinator.Acknowledged.Count;
    landed.Land();
    var flipped = await pass;

    await Assert.That(acknowledgedBeforeLanding).IsEqualTo(0)
      .Because("the completions of the finished batch land under the old epoch before the flip fences them");
    await Assert.That(flipped).IsEquivalentTo([3]);
  }

  [Test]
  public async Task AStreamInFlightWithNoKnownTenure_HoldsNoPartitionBackAsync() {
    var (worker, coordinator, participant, _) = _create();
    participant.InFlight.Add((Guid)TrackedGuid.New());
    coordinator.HandingOver.Add(new PartitionHandover(3, 5, _next));

    var flipped = await worker.HandOverAsync(CancellationToken.None);

    await Assert.That(flipped).IsEquivalentTo([3]).Because("off map mode no stream has a tenure, and nothing hands over there either");
  }

  [Test]
  public async Task APartitionPastTheTable_IsMatchedThroughItsRemainderAsync() {
    var (worker, coordinator, participant, tenures) = _create();
    var busy = (Guid)TrackedGuid.New();
    _claim(tenures, busy, partition: 10_003, epoch: 5);
    coordinator.HandingOver.Add(new PartitionHandover(3, 5, _next));
    participant.InFlight.Add(busy);

    var flipped = await worker.HandOverAsync(CancellationToken.None);

    await Assert.That(flipped).IsEmpty().Because("a row's partition 10,003 is the ownership table's partition 3");
  }

  [Test]
  public async Task DrainChannels_ReportTheStreamsTheirDrainsAreWorkingAsync() {
    var outbox = new OutboxDrainChannel();
    var inbox = new InboxDrainChannel();
    var perspective = new PerspectiveDrainChannel();
    var a = (Guid)TrackedGuid.New();
    var b = (Guid)TrackedGuid.New();
    var c = (Guid)TrackedGuid.New();
    outbox.MarkDraining(a);
    inbox.MarkDraining(b);
    perspective.MarkDraining(c);
    perspective.MarkDraining(a);
    var participant = new DrainChannelsHandoverParticipant(outbox, inbox, perspective);

    await participant.SaveForHandoverAsync(_ => true, CancellationToken.None);

    await Assert.That(participant.StreamsInFlight.ToHashSet()).IsEquivalentTo([a, b, c]);
  }

  // --- helpers --------------------------------------------------------------------------------------

  private static void _claim(PartitionTenures tenures, Guid stream, int partition, long epoch) =>
    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = epoch }, new Dictionary<Guid, int> { [stream] = partition });

  private static (PartitionHandoverWorker Worker, FakeCoordinator Coordinator, FakeParticipant Participant, PartitionTenures Tenures) _create(
      IQueuedWritesLandedSignal? landed = null) {
    var coordinator = new FakeCoordinator();
    var participant = new FakeParticipant();
    var tenures = new PartitionTenures();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var worker = new PartitionHandoverWorker(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      new FixedInstance(), tenures, [participant], landed, NullLogger<PartitionHandoverWorker>.Instance);
    return (worker, coordinator, participant, tenures);
  }

  private sealed class FixedInstance : IServiceInstanceProvider {
    public Guid InstanceId => _self;
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = _self, ServiceName = "test", HostName = "test-host", ProcessId = 1 };
  }

  private sealed class FakeParticipant : IPartitionHandoverParticipant {
    public List<Guid> InFlight { get; } = [];
    public Action<Func<Guid, bool>>? OnSave { get; set; }
    public IEnumerable<Guid> StreamsInFlight => InFlight;
    public Task SaveForHandoverAsync(Func<Guid, bool> handsOver, CancellationToken cancellationToken) {
      OnSave?.Invoke(handsOver);
      return Task.CompletedTask;
    }
  }

  private sealed class GatedLanded : IQueuedWritesLandedSignal {
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Waiting => _waiting.Task;
    public void Land() => _landed.TrySetResult();
    public IReadOnlyList<IQueuedWriteFlusher> Flushers => [];
    public bool AllLanded => _landed.Task.IsCompleted;
    public long PendingWrites => AllLanded ? 0 : 1;
    public Task WaitForAllLandedAsync(CancellationToken cancellationToken = default) {
      _waiting.TrySetResult();
      return _landed.Task;
    }
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    public List<PartitionHandover> HandingOver { get; } = [];
    public List<PartitionHandover> Acknowledged { get; } = [];
    public Action? OnAcknowledge { get; set; }

    public Task<IReadOnlyList<PartitionHandover>> GetPartitionsHandingOverAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PartitionHandover>>([.. HandingOver.Where(h => !Acknowledged.Contains(h))]);

    public Task<IReadOnlyList<int>> AcknowledgeHandoverAsync(
        Guid instanceId, IReadOnlyList<PartitionHandover> handovers, CancellationToken cancellationToken = default) {
      OnAcknowledge?.Invoke();
      Acknowledged.AddRange(handovers);
      return Task.FromResult<IReadOnlyList<int>>([.. handovers.Select(h => h.Partition)]);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
