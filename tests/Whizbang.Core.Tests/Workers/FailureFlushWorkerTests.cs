using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers <see cref="FailureFlushWorker"/>, the batching channel that carries message failures
/// to the work coordinator.
/// </summary>
/// <remarks>
/// Everything reaching this worker is already a failure, so its own failures are invisible by
/// construction: nothing downstream is waiting on a result. A batch dropped on shutdown, or
/// reported under the wrong category, means a message stays marked in-flight forever rather
/// than being retried or dead-lettered -- and no error is raised anywhere to say so.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class FailureFlushWorkerTests {

  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly Lock _gate = new();
    public List<(WorkCategory Category, MessageFailure[] Failures)> Reported { get; } = [];

    public Task ReportFailuresAsync(
        WorkCategory category,
        IReadOnlyList<MessageFailure> failures,
        CancellationToken cancellationToken = default) {
      lock (_gate) {
        Reported.Add((category, [.. failures]));
      }
      return Task.CompletedTask;
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = null,
      });
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static MessageFailure _failure(string error = "boom") => new() {
    MessageId = Guid.NewGuid(),
    CompletedStatus = MessageProcessingStatus.None,
    Error = error,
  };

  private static (FailureFlushWorker Worker, RecordingCoordinator Coordinator) _build(bool enabled = true) {
    var coordinator = new RecordingCoordinator();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var provider = services.BuildServiceProvider();

    var worker = new FailureFlushWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new FailureFlushWorkerOptions { Enabled = enabled }),
      NullLogger<FailureFlushWorker>.Instance);

    return (worker, coordinator);
  }

  [Test]
  public async Task StopAsync_FlushesFailuresThatWereStillBufferedAsync() {
    // The batcher exists to avoid a round trip per failure, which means failures live in memory
    // between flushes. If shutdown discarded that buffer, those messages would stay marked
    // in-flight forever -- never retried, never dead-lettered, and never reported as lost.
    var (worker, coordinator) = _build();
    await worker.StartAsync(CancellationToken.None);

    await worker.EnqueueAsync(WorkCategory.Outbox, _failure());

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Reported.Count).IsGreaterThanOrEqualTo(1)
      .Because("a shutdown that drops buffered failures loses them silently");
    await Assert.That(coordinator.Reported.Sum(r => r.Failures.Length)).IsEqualTo(1);
  }

  [Test]
  public async Task Flush_ReportsEachCategorySeparatelyAsync() {
    // The coordinator writes each category to a different table, so a batch mixing categories
    // would record failures against the wrong work entirely. Grouping is the whole reason
    // CategorizedFailure carries the category alongside the failure.
    var (worker, coordinator) = _build();
    await worker.StartAsync(CancellationToken.None);

    await worker.EnqueueAsync(WorkCategory.Outbox, _failure("outbox-1"));
    await worker.EnqueueAsync(WorkCategory.Inbox, _failure("inbox-1"));
    await worker.EnqueueAsync(WorkCategory.Outbox, _failure("outbox-2"));

    await worker.StopAsync(CancellationToken.None);

    var byCategory = coordinator.Reported
      .GroupBy(r => r.Category)
      .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Failures).ToList());

    await Assert.That(byCategory.ContainsKey(WorkCategory.Outbox)).IsTrue();
    await Assert.That(byCategory.ContainsKey(WorkCategory.Inbox)).IsTrue();
    await Assert.That(byCategory[WorkCategory.Outbox].Count).IsEqualTo(2);
    await Assert.That(byCategory[WorkCategory.Inbox].Count).IsEqualTo(1);
    await Assert.That(coordinator.Reported.All(r => r.Failures.Length > 0)).IsTrue()
      .Because("an empty report is a wasted round trip against the very cost batching exists to avoid");
  }

  [Test]
  public async Task Flush_PreservesTheFailureDetailAsync() {
    // The error text is the only record of why a message failed. Losing it in transit leaves an
    // operator with a failed message and no reason.
    var (worker, coordinator) = _build();
    await worker.StartAsync(CancellationToken.None);
    var failure = _failure("connection reset by peer");

    await worker.EnqueueAsync(WorkCategory.PerspectiveEvent, failure);
    await worker.StopAsync(CancellationToken.None);

    var reported = coordinator.Reported.SelectMany(r => r.Failures).ToList();
    await Assert.That(reported.Count).IsEqualTo(1);
    await Assert.That(reported[0].MessageId).IsEqualTo(failure.MessageId);
    await Assert.That(reported[0].Error).IsEqualTo("connection reset by peer");
  }

  [Test]
  public async Task WhenDisabled_NothingIsReportedAsync() {
    // The killswitch has to stop the write, not merely stop the loop. A disabled worker whose
    // flush callback still fired would keep writing while an operator believed it was halted.
    var (worker, coordinator) = _build(enabled: false);
    await worker.StartAsync(CancellationToken.None);

    await worker.EnqueueAsync(WorkCategory.Outbox, _failure());
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Reported).IsEmpty();
  }

  [Test]
  public async Task WhenDisabled_TheWorkerParksInsteadOfExitingAsync() {
    // A BackgroundService that returns from ExecuteAsync reads to the host as a crashed worker.
    // Parking keeps a deliberately-disabled flusher distinguishable from one that fell over.
    var (worker, _) = _build(enabled: false);

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask is not null).IsTrue();
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled worker stays parked rather than completing, which would look like a crash");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task StopAsync_WithNothingBuffered_ReportsNothingAsync() {
    // An idle service still shuts down. Flushing an empty batch would cost a round trip for no
    // reason on every deployment.
    var (worker, coordinator) = _build();
    await worker.StartAsync(CancellationToken.None);

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Reported).IsEmpty();
  }

  [Test]
  public async Task Constructor_RejectsMissingDependenciesAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
    var options = Options.Create(new FailureFlushWorkerOptions());

    await Assert.That(() => new FailureFlushWorker(
        null!, SchemaReadyGate.AlreadyReady(), options, NullLogger<FailureFlushWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new FailureFlushWorker(
        scopeFactory, null!, options, NullLogger<FailureFlushWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new FailureFlushWorker(
        scopeFactory, SchemaReadyGate.AlreadyReady(), null!, NullLogger<FailureFlushWorker>.Instance))
      .Throws<ArgumentNullException>();
  }
}
