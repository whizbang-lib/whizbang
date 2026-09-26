using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// An <see cref="IWorkCoordinator"/> whose <see cref="ClaimWorkAsync"/> is supplied per test.
/// Every other operation is the interface default (no-op), so a test can drive a claim loop
/// without a database.
/// </summary>
internal sealed class ScriptedWorkCoordinator(Func<int, WorkBatch> claim) : IWorkCoordinator {
  private int _claims;

  /// <summary>Number of times <see cref="ClaimWorkAsync"/> has been entered.</summary>
  public int Claims => Volatile.Read(ref _claims);

  /// <inheritdoc />
  public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
    Task.FromResult(claim(Interlocked.Increment(ref _claims)));

  /// <inheritdoc />
  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkCoordinatorStatistics());

  /// <inheritdoc />
  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
    Task.FromResult<PerspectiveCursorInfo?>(null);

  /// <summary>An empty batch — nothing claimed.</summary>
  public static WorkBatch EmptyBatch => new() {
    OutboxWork = [],
    InboxWork = [],
    PerspectiveWork = []
  };
}
