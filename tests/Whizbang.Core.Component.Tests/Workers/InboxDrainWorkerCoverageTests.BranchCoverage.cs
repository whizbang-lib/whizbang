// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="InboxDrainWorker"/>: the byte budget's off states, the observed-depth
/// fallback cap for a row whose drain key was never requested, the dispatch loop's own cancellation
/// check, and the debug PERF line's "enough rows to be interesting" arm.
/// </summary>
public partial class InboxDrainWorkerCoverageTests {

  /// <summary>
  /// A drain channel that signals when the worker releases a stream's draining marker, which happens
  /// in the batch's finally block: the one point every batch, successful or not, reaches last.
  /// </summary>
  private sealed class DrainedSignalingChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public TaskCompletionSource<Guid> Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void MarkDrained(Guid streamId) => Drained.TrySetResult(streamId);
  }

  /// <summary>
  /// A coordinator that implements the byte-budgeted fetch overload, so the budget the worker passes
  /// is observable, and whose single scripted response may run a side effect (a host stop) first.
  /// </summary>
  private sealed class ByteBudgetCapturingCoordinator : IWorkCoordinator {
    public Func<IReadOnlyList<Guid>, IReadOnlyList<InboxBatchRow>> Respond { get; set; } = _ => [];
    public Action? DuringFetch { get; set; }
    public List<long?> MaxBytesSeen { get; } = [];
    public int CallCount { get; private set; }
    public TaskCompletionSource Fetched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes,
        CancellationToken cancellationToken = default) {
      CallCount++;
      IReadOnlyList<InboxBatchRow> response = CallCount == 1 ? Respond(streamIds) : [];
      MaxBytesSeen.Add(maxBytes);
      DuringFetch?.Invoke();
      Fetched.TrySetResult();
      return Task.FromResult(response);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static async Task<long?> _byteBudgetPassedToFetchAsync(long? configured) {
    var streamId = (Guid)TrackedGuid.New();
    var coord = new ByteBudgetCapturingCoordinator();
    var drain = new DrainedSignalingChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drain, new CapturingInboxChannel(), gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 10, MaxBytesPerStream = configured }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId, cts.Token);
    await coord.Fetched.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    return coord.MaxBytesSeen[0];
  }

  [Test]
  public async Task DrainStreamBatch_ByteBudgetUnset_FetchesUnboundedByBytesAsync() {
    var passed = await _byteBudgetPassedToFetchAsync(null);
    await Assert.That(passed).IsNull()
      .Because("an unset byte budget means the fetch is bounded by row count alone");
  }

  [Test]
  public async Task DrainStreamBatch_ByteBudgetZero_IsTreatedAsOffRatherThanStarvingEveryFetchAsync() {
    var passed = await _byteBudgetPassedToFetchAsync(0);
    await Assert.That(passed).IsNull()
      .Because("a misconfigured zero must disable the budget, not shrink every fetch to a single row per stream");
  }

  [Test]
  public async Task DrainStreamBatch_ByteBudgetPositive_IsPassedThroughToTheFetchAsync() {
    var passed = await _byteBudgetPassedToFetchAsync(65_536);
    await Assert.That(passed).IsEqualTo(65_536L);
  }

  [Test]
  public async Task DrainStreamBatch_RowForAStreamThatWasNeverRequested_IsCreditedAtTheEffectivePageSizeAsync() {
    // The fetch can return a row whose drain key the plan never assigned a cap to (a row keyed by a
    // stream other than the requested one). Its depth must still be observed, against the page size
    // the next fetch would request, or the next cycle's allocation for it is computed from nothing.
    var requested = (Guid)TrackedGuid.New();
    var foreign = (Guid)TrackedGuid.New();
    var coord = new ByteBudgetCapturingCoordinator {
      Respond = _ => [_row((Guid)TrackedGuid.New(), foreign)],
    };
    var drain = new DrainedSignalingChannel();
    var inbox = new CapturingInboxChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 1,
        MaxPerStreamCeiling = 1000,
        MinRowsPerStream = 1,
        AdaptivePerStreamEnabled = false,
      }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(requested, cts.Token);
    await drain.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ObservedDepthCountForTest).IsEqualTo(1)
      .Because("only the stream that actually returned a row is recorded; the requested stream came back empty");
    // One row against the effective page size of 1 is a saturated page, credited as depth 2, which
    // the next plan turns into a page of 2. A fallback cap of anything else would plan a page of 1.
    var plan = worker.PlanFetchesForTest([foreign]);
    await Assert.That(plan).Count().IsEqualTo(1);
    await Assert.That(plan[0].Cap).IsEqualTo(2);
    await Assert.That(inbox.Written).IsEmpty()
      .Because("dispatch walks the requested streams only, so a row keyed elsewhere is not handed over here");
  }

  [Test]
  public async Task DrainStreamBatch_StopArrivesDuringTheFetch_DispatchesNothingFromThePageAsync() {
    // The page is in hand when the host stops. The dispatch loop checks the token before each stream,
    // so a stopping worker hands nothing more to a working set that is itself shutting down; the
    // rows stay leased and are re-offered by the claim backstop.
    var streamId = (Guid)TrackedGuid.New();
    using var cts = new CancellationTokenSource();
    var coord = new ByteBudgetCapturingCoordinator {
      Respond = sids => [.. sids.Select(sid => _row((Guid)TrackedGuid.New(), sid))],
      DuringFetch = cts.Cancel,
    };
    var drain = new DrainedSignalingChannel();
    var inbox = new CapturingInboxChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 10, AdaptivePerStreamEnabled = false }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId, CancellationToken.None);
    await drain.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(coord.CallCount).IsEqualTo(1);
    await Assert.That(inbox.Written).IsEmpty()
      .Because("the stop landed before dispatch began, so not one row of the fetched page may be handed over");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
  }

  [Test]
  public async Task DrainStreamInner_FiveOrMoreRowsEnqueued_WritesThePerfDebugLineAsync() {
    // The PERF line is gated on the drain being worth reading about. Five rows enqueued by the
    // loop-until-empty path qualifies on count alone, whatever the wall time.
    var streamId = (Guid)TrackedGuid.New();
    var coord = new ScriptedWorkCoordinator();
    // First pass saturates the floor cap of 5, handing the stream to the inner loop.
    coord.Enqueue(_ => [.. Enumerable.Range(0, 5).Select(_ => _row((Guid)TrackedGuid.New(), streamId))]);
    // Inner loop: a full page of 5 fresh rows, then an empty page that ends the drain.
    coord.Enqueue(_ => [.. Enumerable.Range(0, 5).Select(_ => _row((Guid)TrackedGuid.New(), streamId))]);

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel();
    var logger = new EventIdSignalingLogger<InboxDrainWorker>();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 5, AdaptivePerStreamEnabled = false }),
      _jsonOpts,
      logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId, cts.Token);
    // Unstructured LogDebug calls carry event id 0; the source-generated lines all have their own ids.
    await logger.WhenLoggedAsync(0, TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    var perf = logger.LinesWith(0).Where(l => l.Message.Contains("PERF InboxDrain", StringComparison.Ordinal)).ToList();
    await Assert.That(perf).Count().IsEqualTo(1);
    await Assert.That(perf[0].Message).Contains("enqueued=5", StringComparison.Ordinal);
    await Assert.That(perf[0].Message).Contains("fetches=2", StringComparison.Ordinal);
  }

  /// <summary>An inbox writer whose every write suspends before it records, as a bounded channel under load does.</summary>
  private sealed class YieldingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    private readonly List<Guid> _written = [];
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public List<Guid> Written {
      get {
        lock (_written) { return [.. _written]; }
      }
    }
    public async ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      await Task.Yield();
      lock (_written) { _written.Add(work.MessageId); }
      await _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { /* nothing tracked */ }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  /// <summary>A coordinator whose every fetch suspends before answering from its script, as a database round trip does.</summary>
  private sealed class YieldingFetchCoordinator : IWorkCoordinator {
    private int _calls;
    public Func<int, IReadOnlyList<InboxBatchRow>> Respond { get; init; } = _ => [];
    public int CallCount => Volatile.Read(ref _calls);

    public async Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes,
        CancellationToken cancellationToken = default) {
      await Task.Yield();
      var call = Interlocked.Increment(ref _calls);
      return Respond(call);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  [Test]
  public async Task DrainStreamBatch_WriteAndTailDrainBothSuspend_DispatchesThePageInOrderThenDrainsTheTailAsync() {
    // Every await in the per-stream dispatch block (the channel write and the loop-until-empty tail
    // drain) suspends once here, as a real channel and database do. The other drain tests complete
    // both synchronously, so the dispatch block's resume paths had never run.
    var streamId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    var coord = new YieldingFetchCoordinator {
      // Call 1 is the batched fetch and fills the cap of 2, which hands the stream to the tail
      // drain; call 2 is the tail drain's fetch and finds nothing more.
      Respond = call => call == 1 ? [_row(second, streamId), _row(first, streamId)] : [],
    };
    var drain = new DrainedSignalingChannel();
    var inbox = new YieldingInboxChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 2,
        MaxPerStreamCeiling = 2,
        AdaptivePerStreamEnabled = false,
      }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId, cts.Token);
    await drain.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    Guid[] expectedOrder = [.. new[] { first, second }.Order()];
    var written = inbox.Written;
    await Assert.That(written).Count().IsEqualTo(2)
      .Because("both rows are handed over, each after its suspended write resumes");
    await Assert.That(written[0]).IsEqualTo(expectedOrder[0]);
    await Assert.That(written[1]).IsEqualTo(expectedOrder[1])
      .Because("the page is dispatched in message-id order even though the fetch returned it reversed");
    await Assert.That(coord.CallCount).IsEqualTo(2)
      .Because("a page that filled the cap must still reach the tail drain after the writes resumed");
  }
}
