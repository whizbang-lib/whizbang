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
/// <remarks>
/// Initializes a new instance of WorkBatchCoordinator.
/// </remarks>
/// <param name="workCoordinator">The work coordinator for database operations</param>
/// <param name="instanceProvider">Service instance provider for service details</param>
/// <param name="outboxChannel">Channel writer for outbox work distribution</param>
/// <param name="perspectiveChannel">Channel writer for perspective work distribution</param>
public class WorkBatchCoordinator(
  IWorkCoordinator workCoordinator,
  IServiceInstanceProvider instanceProvider,
  IWorkChannelWriter outboxChannel,
  IPerspectiveChannelWriter perspectiveChannel
  ) : IWorkBatchCoordinator {
  private readonly IWorkCoordinator _workCoordinator = workCoordinator ?? throw new ArgumentNullException(nameof(workCoordinator));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IWorkChannelWriter _outboxChannel = outboxChannel ?? throw new ArgumentNullException(nameof(outboxChannel));
  private readonly IPerspectiveChannelWriter _perspectiveChannel = perspectiveChannel ?? throw new ArgumentNullException(nameof(perspectiveChannel));

  /// <inheritdoc />
  public async Task ProcessAndDistributeAsync(
    Guid instanceId,
    List<MessageCompletion>? outboxCompletions = null,
    List<MessageFailure>? outboxFailures = null,
    List<MessageCompletion>? inboxCompletions = null,
    List<MessageFailure>? inboxFailures = null,
    List<PerspectiveCursorCompletion>? perspectiveCompletions = null,
    List<PerspectiveCursorFailure>? perspectiveFailures = null,
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
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [.. (perspectiveFailures ?? [])],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchFlags.None,
      PartitionCount = 16,  // FUTURE: Make configurable
      LeaseSeconds = 30,    // FUTURE: Make configurable
      StaleThresholdSeconds = 300  // FUTURE: Make configurable
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

    // FUTURE: Distribute inbox work to IInboxChannelWriter when implemented (similar to perspective work distribution above)
  }
}
