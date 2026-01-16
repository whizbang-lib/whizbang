using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IWorkBatchCoordinator.
/// Calls IWorkCoordinator.ProcessWorkBatchAsync() and distributes work to channels.
/// This implements the central pattern: ONE SQL call → multiple channel distribution.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs</tests>
public class WorkBatchCoordinator : IWorkBatchCoordinator {
  private readonly IWorkCoordinator _workCoordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IWorkChannelWriter _outboxChannel;
  private readonly IPerspectiveChannelWriter _perspectiveChannel;

  /// <summary>
  /// Initializes a new instance of WorkBatchCoordinator.
  /// </summary>
  /// <param name="workCoordinator">The work coordinator for database operations</param>
  /// <param name="instanceProvider">Service instance provider for service details</param>
  /// <param name="outboxChannel">Channel writer for outbox work distribution</param>
  /// <param name="perspectiveChannel">Channel writer for perspective work distribution</param>
  public WorkBatchCoordinator(
    IWorkCoordinator workCoordinator,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter outboxChannel,
    IPerspectiveChannelWriter perspectiveChannel
  ) {
    _workCoordinator = workCoordinator ?? throw new ArgumentNullException(nameof(workCoordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _outboxChannel = outboxChannel ?? throw new ArgumentNullException(nameof(outboxChannel));
    _perspectiveChannel = perspectiveChannel ?? throw new ArgumentNullException(nameof(perspectiveChannel));
  }

  /// <inheritdoc />
  public async Task ProcessAndDistributeAsync(
    Guid instanceId,
    List<MessageCompletion>? outboxCompletions = null,
    List<MessageFailure>? outboxFailures = null,
    List<MessageCompletion>? inboxCompletions = null,
    List<MessageFailure>? inboxFailures = null,
    List<PerspectiveCheckpointCompletion>? perspectiveCompletions = null,
    List<PerspectiveCheckpointFailure>? perspectiveFailures = null,
    CancellationToken ct = default
  ) {
    // Call the central SQL function (process_work_batch)
    var request = new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = _instanceProvider.ServiceName,
      HostName = _instanceProvider.HostName,
      ProcessId = _instanceProvider.ProcessId,
      Metadata = null,
      OutboxCompletions = [.. (outboxCompletions ?? [])],
      OutboxFailures = [.. (outboxFailures ?? [])],
      InboxCompletions = [.. (inboxCompletions ?? [])],
      InboxFailures = [.. (inboxFailures ?? [])],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [.. (perspectiveCompletions ?? [])],
      PerspectiveFailures = [.. (perspectiveFailures ?? [])],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchFlags.None,
      PartitionCount = 16,  // TODO: Make configurable
      LeaseSeconds = 30,    // TODO: Make configurable
      StaleThresholdSeconds = 300  // TODO: Make configurable
    };
    var workBatch = await _workCoordinator.ProcessWorkBatchAsync(request, ct);

    // Distribute outbox work to outbox channel
    foreach (var outboxWork in workBatch.OutboxWork) {
      await _outboxChannel.WriteAsync(outboxWork, ct);
    }

    // Distribute perspective work to perspective channel
    foreach (var perspectiveWork in workBatch.PerspectiveWork) {
      await _perspectiveChannel.WriteAsync(perspectiveWork, ct);
    }

    // TODO: Distribute inbox work when IInboxChannelWriter is created
    // foreach (var inboxWork in workBatch.InboxWork) {
    //   await _inboxChannel.WriteAsync(inboxWork, ct);
    // }
  }
}
