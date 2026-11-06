// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A coordinator whose integrity delivery queue lives in memory, with the semantics of migration 302 (recorded once,
/// leased oldest first, completed by delete, failed with a counted attempt), so the deferral and the drain can be
/// tested without a database. <see cref="Supported"/> false makes it a coordinator without the queue.
/// </summary>
internal sealed class InMemoryIntegrityDeliveryCoordinator : IWorkCoordinator {
  private readonly List<IntegrityDelivery> _queue = [];

  public bool Supported { get; init; } = true;

  /// <summary>Every enqueue call, recorded or not.</summary>
  public List<(Guid Id, string Type, string Payload)> Enqueued { get; } = [];

  public List<Guid> Completed { get; } = [];

  public List<(Guid Id, string Error, TimeSpan Backoff)> Failed { get; } = [];

  public List<int> LeaseSizes { get; } = [];

  public IReadOnlyList<IntegrityDelivery> Pending => _queue;

  /// <summary>The settledness reading the drain is given; null = unmeasured.</summary>
  public ServiceBacklog? Backlog { get; set; } = new();

  public Exception? BacklogFailure { get; set; }

  public Task<IntegrityDeliveryEnqueueResult> EnqueueIntegrityDeliveryAsync(
      Guid deliveryId, string deliveryType, string payload, CancellationToken cancellationToken = default) {
    if (EnqueueFailure is { } failure) {
      return Task.FromException<IntegrityDeliveryEnqueueResult>(failure);
    }
    if (!Supported) {
      return Task.FromResult(IntegrityDeliveryEnqueueResult.Unsupported);
    }
    Enqueued.Add((deliveryId, deliveryType, payload));
    if (_queue.Exists(d => d.DeliveryId == deliveryId)) {
      return Task.FromResult(IntegrityDeliveryEnqueueResult.AlreadyRecorded);
    }
    _queue.Add(new IntegrityDelivery(deliveryId, deliveryType, payload, 0, DateTimeOffset.UnixEpoch.AddTicks(_queue.Count)));
    return Task.FromResult(IntegrityDeliveryEnqueueResult.Recorded);
  }

  /// <summary>Adds a delivery directly, as if recorded on another instance.</summary>
  public void Seed(Guid id, string type, string payload, int attempts = 0) =>
    _queue.Add(new IntegrityDelivery(id, type, payload, attempts, DateTimeOffset.UnixEpoch.AddTicks(_queue.Count)));

  // Leased rows are invisible until completed or failed, as the lease's not_before push makes them.
  private readonly HashSet<Guid> _leased = [];

  public Task<IReadOnlyList<IntegrityDelivery>> LeaseIntegrityDeliveriesAsync(
      int maxDeliveries, TimeSpan lease, CancellationToken cancellationToken = default) {
    LeaseSizes.Add(maxDeliveries);
    var due = _queue.Where(d => !_leased.Contains(d.DeliveryId)).Take(maxDeliveries).ToList();
    foreach (var d in due) {
      _leased.Add(d.DeliveryId);
    }
    return Task.FromResult<IReadOnlyList<IntegrityDelivery>>(due);
  }

  public Task CompleteIntegrityDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default) {
    Completed.Add(deliveryId);
    _queue.RemoveAll(d => d.DeliveryId == deliveryId);
    _leased.Remove(deliveryId);
    return Task.CompletedTask;
  }

  /// <summary>Failed rows stay leased (held for their backoff) for the rest of the test.</summary>
  public Task FailIntegrityDeliveryAsync(Guid deliveryId, string failure, TimeSpan backoff, CancellationToken cancellationToken = default) {
    Failed.Add((deliveryId, failure, backoff));
    var i = _queue.FindIndex(d => d.DeliveryId == deliveryId);
    _queue[i] = _queue[i] with { Attempts = _queue[i].Attempts + 1 };
    return Task.CompletedTask;
  }

  public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
    BacklogFailure is { } failure ? ValueTask.FromException<ServiceBacklog?>(failure) : ValueTask.FromResult(Backlog);

  /// <summary>Every origin adoption the reconcile asked for.</summary>
  public List<(Guid Origin, string? Name, IReadOnlyList<Guid> EventIds, IReadOnlyList<long?> Sequences)> Adoptions { get; } = [];

  public Exception? EnqueueFailure { get; set; }

  public Task<IReadOnlyList<RedeliveredBucketPresence>?> AdoptRedeliveredOriginAsync(
      Guid originServiceId, string? originServiceName, IReadOnlyList<Guid> eventIds, IReadOnlyList<long?> commitSequences,
      CancellationToken cancellationToken = default) {
    Adoptions.Add((originServiceId, originServiceName, eventIds, commitSequences));
    return Task.FromResult<IReadOnlyList<RedeliveredBucketPresence>?>(AdoptionAnswer);
  }

  /// <summary>Every batch reconcile the drain asked for (#2099).</summary>
  public List<IReadOnlyList<RedeliveredBundle>> BatchReconciles { get; } = [];

  /// <summary>Answers a batch reconcile per bundle; null makes the store one without the batch call.</summary>
  public Func<IReadOnlyList<RedeliveredBundle>, IReadOnlyList<IReadOnlyList<RedeliveredBucketPresence>>>? BatchAnswer { get; set; }

  public Exception? BatchFailure { get; set; }

  public Task<IReadOnlyList<IReadOnlyList<RedeliveredBucketPresence>>?> ReconcileRedeliveredBundlesAsync(
      IReadOnlyList<RedeliveredBundle> bundles, CancellationToken cancellationToken = default) {
    BatchReconciles.Add(bundles);
    if (BatchFailure is { } failure) {
      return Task.FromException<IReadOnlyList<IReadOnlyList<RedeliveredBucketPresence>>?>(failure);
    }
    return Task.FromResult(BatchAnswer?.Invoke(bundles));
  }

  /// <summary>What the store answers an adoption with.</summary>
  public IReadOnlyList<RedeliveredBucketPresence> AdoptionAnswer { get; set; } = [];

  /// <summary>What the store answers a bundle's commit-time record with; null = unsupported.</summary>
  public IntegrityBundleAnswer? BundleAnswer { get; set; }

  public Exception? BundleAnswerFailure { get; set; }

  /// <summary>Every commit-time record the reconcile asked for.</summary>
  public List<(Guid Origin, IReadOnlyList<Guid> EventIds, IReadOnlyList<long?> Sequences, DateTimeOffset Now)> BundleAnswers { get; } = [];

  public Task<IntegrityBundleAnswer?> IntegrityRecordBundleAnswerAsync(
      Guid originServiceId, IReadOnlyList<Guid> eventIds, IReadOnlyList<long?> commitSequences, DateTimeOffset now,
      CancellationToken cancellationToken = default) {
    BundleAnswers.Add((originServiceId, eventIds, commitSequences, now));
    return BundleAnswerFailure is { } failure
      ? Task.FromException<IntegrityBundleAnswer?>(failure)
      : Task.FromResult(BundleAnswer);
  }

  public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
}

/// <summary>Records every registration, receptor included.</summary>
internal sealed class RecordingReceptorRegistry : IReceptorRegistry {
  public List<(Type Msg, LifecycleStage Stage, object Receptor)> Registered { get; } = [];
  public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
    Registered.Add((typeof(TMessage), stage, receptor));
  public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
    Registered.Add((typeof(TMessage), stage, receptor));
  public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
  public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
}

/// <summary>A receptor that records what it handled and can be told to fail.</summary>
internal sealed class RecordingReceptor<TMessage> : IReceptor<TMessage> where TMessage : IMessage {
  public List<TMessage> Handled { get; } = [];
  public List<bool> HandledInsideTheDrain { get; } = [];
  public Exception? Failure { get; set; }

  public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    Handled.Add(message);
    HandledInsideTheDrain.Add(IntegrityDrainContext.IsActive);
    return Failure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
  }
}
