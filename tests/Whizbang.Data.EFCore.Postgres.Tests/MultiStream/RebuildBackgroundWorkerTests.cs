// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.MultiStream;

/// <summary>
/// The background rebuild queue (#1353): a whole-perspective rebuild handed off by the rebuild command's handler runs
/// here, on the host's own lifetime, not under the inbox handler's budget. Each queued rebuild runs in its own scope and
/// is reported; a failure is reported and the next queued rebuild still runs. Waits are on the worker's own signals.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
[Category("Shard1")]
public class RebuildBackgroundWorkerTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);
  private static readonly RebuildOrigin _origin = new(Guid.Parse("019a1000-0000-7000-8000-0000000000ee"), RebuildTrigger.Requested, null);

  private static (RebuildBackgroundWorker Worker, RebuildBackgroundQueue Queue, ThrowingOnceRebuilder Rebuilder) _worker(
      bool failFirst = false, bool blockUntilCanceled = false, bool cancelOnItsOwn = false) {
    var rebuilder = new ThrowingOnceRebuilder { FailFirst = failFirst, BlockUntilCanceled = blockUntilCanceled, CancelOnItsOwn = cancelOnItsOwn };
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    var sp = services.BuildServiceProvider();
    var queue = new RebuildBackgroundQueue();
    return (new RebuildBackgroundWorker(queue, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RebuildBackgroundWorker>.Instance), queue, rebuilder);
  }

  [Test]
  public async Task AQueuedRebuild_IsRunByTheWorkerAndReportedAsync() {
    var (worker, queue, rebuilder) = _worker();
    var finished = new TaskCompletionSource<RebuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnRebuildFinished += r => finished.TrySetResult(r);

    await worker.StartAsync(CancellationToken.None);
    try {
      queue.Enqueue("Test.Balance", _origin);
      var result = await finished.Task.WaitAsync(_deadline);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(rebuilder.Calls.Single()).IsEqualTo(("Test.Balance", _origin));
      await Assert.That(queue.Pending).IsEqualTo(0);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task ARebuildThatThrows_IsReportedAndTheNextQueuedRebuildStillRunsAsync() {
    var (worker, queue, rebuilder) = _worker(failFirst: true);
    var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finished = new TaskCompletionSource<RebuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnRebuildFailed += (_, ex) => failed.TrySetResult(ex);
    worker.OnRebuildFinished += r => finished.TrySetResult(r);

    await worker.StartAsync(CancellationToken.None);
    try {
      queue.Enqueue("Test.First", _origin);
      queue.Enqueue("Test.Second", _origin);

      await Assert.That(await failed.Task.WaitAsync(_deadline)).IsTypeOf<InvalidOperationException>();
      await Assert.That((await finished.Task.WaitAsync(_deadline)).PerspectiveName).IsEqualTo("Test.Second")
        .Because("one failed rebuild never stops the queue");
      await Assert.That(rebuilder.Calls.Select(c => c.Perspective)).IsEquivalentTo(["Test.First", "Test.Second"]);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Stopping_EndsTheWorkerWithRebuildsStillQueuedAsync() {
    var (worker, queue, rebuilder) = _worker();
    await worker.StartAsync(CancellationToken.None);

    await worker.StopAsync(CancellationToken.None);
    queue.Enqueue("Test.Late", _origin);

    // Stopped before the worker body was scheduled, ExecuteTask ends canceled; either way it must end.
    await worker.ExecuteTask!.WaitAsync(_deadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(rebuilder.Calls).IsEmpty();
  }

  [Test]
  public async Task StoppingDuringARebuild_EndsTheWorkerWithoutReportingAFailureAsync() {
    var (worker, queue, rebuilder) = _worker(blockUntilCanceled: true);
    var failures = new List<Exception>();
    worker.OnRebuildFailed += (_, ex) => failures.Add(ex);

    await worker.StartAsync(CancellationToken.None);
    queue.Enqueue("Test.Long", _origin);
    await rebuilder.Entered.WaitAsync(_deadline);
    await worker.StopAsync(CancellationToken.None);

    await worker.ExecuteTask!.WaitAsync(_deadline);
    await Assert.That(failures).IsEmpty()
      .Because("a rebuild cut short by shutdown is not a failed rebuild: blue-green swaps nothing until it finishes");
  }

  [Test]
  public async Task ARebuildCanceledOnItsOwn_IsReportedAsAFailureAsync() {
    var (worker, queue, _) = _worker(cancelOnItsOwn: true);
    var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnRebuildFailed += (_, ex) => failed.TrySetResult(ex);

    await worker.StartAsync(CancellationToken.None);
    try {
      queue.Enqueue("Test.Timeout", _origin);

      await Assert.That(await failed.Task.WaitAsync(_deadline)).IsAssignableTo<OperationCanceledException>()
        .Because("only the host stopping ends a rebuild quietly; a timeout inside it is a failure to report");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Constructor_NullDependencies_ThrowAsync() {
    var sp = new ServiceCollection().BuildServiceProvider();
    var scopes = sp.GetRequiredService<IServiceScopeFactory>();
    var logger = NullLogger<RebuildBackgroundWorker>.Instance;

    await Assert.That(() => new RebuildBackgroundWorker(null!, scopes, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new RebuildBackgroundWorker(new RebuildBackgroundQueue(), null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new RebuildBackgroundWorker(new RebuildBackgroundQueue(), scopes, null!)).Throws<ArgumentNullException>();
  }

  internal sealed class ThrowingOnceRebuilder : IPerspectiveRebuilder {
    private int _calls;

    public bool FailFirst { get; init; }

    public bool BlockUntilCanceled { get; init; }

    public bool CancelOnItsOwn { get; init; }

    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public List<(string Perspective, RebuildOrigin Origin)> Calls { get; } = [];

    public async Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) {
      lock (Calls) {
        Calls.Add((perspectiveName, origin));
      }
      _entered.TrySetResult();
      if (BlockUntilCanceled) {
        await Task.Delay(Timeout.Infinite, ct);
      }
      if (CancelOnItsOwn) {
        throw new TaskCanceledException("timed out inside the rebuild");
      }
      if (FailFirst && Interlocked.Increment(ref _calls) == 1) {
        throw new InvalidOperationException("shadow table could not be built");
      }
      return new RebuildResult(perspectiveName, 1, 1, TimeSpan.Zero, Success: true, Error: null);
    }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, RebuildOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
  }
}
