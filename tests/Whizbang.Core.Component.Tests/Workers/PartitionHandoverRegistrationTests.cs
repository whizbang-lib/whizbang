// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The handover worker as the turnkey pipeline registers it (#2557): its participants are the drain channels and the
/// perspective worker, plus any a host adds, and a pass that hands a partition over asks every one of them to save what
/// it holds there before it acknowledges.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
public sealed class PartitionHandoverRegistrationTests {
  [Test]
  [Timeout(30000)]
  public async Task TheRegisteredWorker_AsksEveryRegisteredParticipant_OnAHandoverPassAsync(CancellationToken cancellationToken) {
    var coordinator = new HandingOverCoordinator();
    var recorder = new RecordingParticipant();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    // What a host's own registrations (AddWhizbang, the generated registries) supply and the worker pipeline does not.
    services.AddSingleton(new JsonSerializerOptions());
    services.AddSingleton<IPerspectiveSyncSignaler>(new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance));
    services.AddSingleton<ISyncEventTracker>(new SyncEventTracker());
    services.AddSingleton<ITimeProvider>(new SystemTimeProvider(new FakeTimeProvider()));
    services.AddSingleton<IPartitionHandoverParticipant>(recorder);
    services.AddWhizbangWorkers();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    await using var provider = services.BuildServiceProvider();

    var flipped = await provider.GetRequiredService<PartitionHandoverWorker>().HandOverAsync(cancellationToken);

    await Assert.That(flipped).IsEquivalentTo([3]).Because("no participant works a stream of the partition, so it is handed over");
    await Assert.That(recorder.Saves).IsEqualTo(1)
      .Because("the worker enumerates the container's participants on the pass, so a host's own participant is asked too");
    var participants = provider.GetServices<IPartitionHandoverParticipant>().ToList();
    await Assert.That(participants.OfType<DrainChannelsHandoverParticipant>()).Count().IsEqualTo(1)
      .Because("the drain channels take part in every handover the pipeline registers");
    await Assert.That(participants.OfType<PerspectiveWorker>()).Count().IsEqualTo(1)
      .Because("the perspective worker saves its write-behind models before a partition moves");
  }

  private sealed class RecordingParticipant : IPartitionHandoverParticipant {
    private int _saves;
    public int Saves => Volatile.Read(ref _saves);
    public IEnumerable<Guid> StreamsInFlight => [];
    public Task SaveForHandoverAsync(Func<Guid, bool> handsOver, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _saves);
      return Task.CompletedTask;
    }
  }

  private sealed class HandingOverCoordinator : IWorkCoordinator {
    public Task<IReadOnlyList<PartitionHandover>> GetPartitionsHandingOverAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PartitionHandover>>([new PartitionHandover(3, 5, Guid.Empty)]);

    public Task<IReadOnlyList<int>> AcknowledgeHandoverAsync(
        Guid instanceId, IReadOnlyList<PartitionHandover> handovers, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<int>>([.. handovers.Select(h => h.Partition)]);

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
