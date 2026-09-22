using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Inbox completions a strategy queues are drained by the flush (#734). The helper routed outbox
/// completions, outbox failures and inbox failures to their channels and dropped inbox completions on the
/// floor: counted in the empty-flush short-circuit, logged as queued, never committed, so a Service Bus
/// consumer's handled rows stayed leased and unprocessed until they lapsed and were re-offered. A queued
/// inbox completion now lands as a handler commit on the commit channel, or directly on the coordinator
/// when the flush runs without a scope.
/// </summary>
/// <docs>messaging/work-coordinator#inbox-completions</docs>
[Category("Unit")]
public class WorkCoordinatorFlushHelperInboxCompletionTests {

  private sealed class RecordingCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentBag<HandlerCommitRequest> Requests { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      Requests.Add(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingCoordinator : IWorkCoordinator {
    public ConcurrentBag<HandlerCommitRequest> DirectCommits { get; } = [];
    public Task CommitHandlerResultAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      DirectCommits.Add(request);
      return Task.CompletedTask;
    }
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "flush-svc";
    public string HostName => "flush-host";
    public int ProcessId => 7;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private static MessageCompletion _completion(MessageProcessingStatus status = MessageProcessingStatus.Published) =>
    new() { MessageId = (Guid)TrackedGuid.NewMedo(), Status = status };

  private static FlushContext _ctx(IWorkCoordinator? coordinator, IServiceScopeFactory? scopeFactory, IServiceInstanceProvider instance, MessageCompletion[] inboxCompletions) => new(
    coordinator, scopeFactory, instance, new WorkCoordinatorOptions { DebugMode = true }, "test",
    OutboxMessages: [], InboxMessages: [], OutboxCompletions: [], InboxCompletions: inboxCompletions,
    OutboxFailures: [], InboxFailures: [], Flags: WorkBatchOptions.None,
    LifecycleMessageDeserializer: null, Logger: null, TracingOptions: null, Metrics: null, LifecycleMetrics: null,
    WorkChannelWriter: null, PendingAuditMessages: null, SkipLifecycle: true);

  [Test]
  public async Task ScopePath_InboxCompletions_LandOnTheHandlerCommitChannelAsync() {
    var channel = new RecordingCommitChannel();
    var instance = new FakeInstanceProvider();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new RecordingCoordinator());
    services.AddSingleton<IInboxHandlerCommitChannel>(channel);
    await using var sp = services.BuildServiceProvider();
    var completions = new[] { _completion(), _completion(MessageProcessingStatus.EventStored), _completion() };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: null, sp.GetRequiredService<IServiceScopeFactory>(), instance, completions), default);

    await Assert.That(channel.Requests.Count).IsEqualTo(3)
      .Because("every queued inbox completion must reach the commit path; a completion that is counted and dropped leaves the row leased until it lapses (#734)");
    foreach (var c in completions) {
      var request = channel.Requests.Single(r => r.HandlerId == c.MessageId);
      await Assert.That(request.InboxCompletion.MessageId).IsEqualTo(c.MessageId);
      await Assert.That(request.InboxCompletion.Status).IsEqualTo((int)c.Status);
      await Assert.That(request.InstanceId).IsEqualTo(instance.InstanceId).Because("the commit names the instance that handled the row");
      await Assert.That(request.DebugMode).IsTrue().Because("the coordinator's debug mode rides on the request as it does for dispatched handlers");
    }
  }

  [Test]
  public async Task DirectCoordinatorPath_InboxCompletions_AreCommittedOnTheCoordinatorAsync() {
    var coordinator = new RecordingCoordinator();
    var instance = new FakeInstanceProvider();
    var completions = new[] { _completion(), _completion() };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator, scopeFactory: null, instance, completions), default);

    await Assert.That(coordinator.DirectCommits.Count).IsEqualTo(2)
      .Because("without a scope there is no channel; the completion still has to land, so it commits on the coordinator itself");
    await Assert.That(coordinator.DirectCommits.Select(r => r.HandlerId).ToList()).IsEquivalentTo(completions.Select(c => c.MessageId).ToList());
  }
}
