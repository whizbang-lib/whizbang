// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.MultiStream;

/// <summary>
/// The multi-stream command worker's hosted loop (#1358): it waits for the schema, wakes on a new-run notice, passes again
/// at once after a full batch, survives a failed pass, and ends on shutdown. Every wait is on the signal for the exact
/// transition asserted; the poll interval is an hour, so nothing here passes by polling.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#worker</docs>
[Category("Component")]
[Category("MultiStream")]
public class MultiStreamCommandWorkerLoopTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);
  private static readonly Guid _instance = Guid.Parse("0199e000-0000-7000-8000-000000000001");
  private static readonly string _commandType = TypeNameFormatter.Format(typeof(LoopProbeCommand));

  private static MultiStreamWorkItem _item(Guid run, Guid stream) =>
    new(run, stream, _commandType, MultiStreamJson.Serialize(new LoopProbeCommand(run, [stream])), null, 1);

  private static MultiStreamCommandWorker _worker(
      LoopStore store, MultiStreamCommandOptions? options = null, ISchemaReadyGate? gate = null, LoopHandler? handler = null,
      bool withHandler = true) {
    var services = new ServiceCollection();
    if (withHandler) {
      services.AddMultiStreamCommandHandler<LoopProbeCommand, LoopHandler>();
      services.AddSingleton(handler ?? new LoopHandler());
    }
    var provider = services.BuildServiceProvider();
    options ??= new MultiStreamCommandOptions();
    options.PollInterval = TimeSpan.FromHours(1);
    return new MultiStreamCommandWorker(
      store,
      provider.GetServices<IMultiStreamCommandBinding>(),
      provider.GetRequiredService<IServiceScopeFactory>(),
      new LoopInstance(),
      Options.Create(options),
      NullLogger<MultiStreamCommandWorker>.Instance,
      schemaReady: gate);
  }

  private static (Task<int> Pass, Action Unsubscribe) _nextPass(MultiStreamCommandWorker worker, Func<int, bool>? when = null) {
    var pass = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    void Handler(int claimed) {
      if (when is null || when(claimed)) {
        pass.TrySetResult(claimed);
      }
    }
    worker.OnPassCompleted += Handler;
    return (pass.Task, () => worker.OnPassCompleted -= Handler);
  }

  [Test]
  public async Task Loop_ANewRunNotice_WakesTheWorkerAsync() {
    var store = new LoopStore();
    var worker = _worker(store);
    var (first, stopFirst) = _nextPass(worker);
    var run = Guid.CreateVersion7();
    var completed = new TaskCompletionSource<MultiStreamRunCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnRunCompleted += c => completed.TrySetResult(c);

    await worker.StartAsync(CancellationToken.None);
    try {
      await Assert.That(await first.WaitAsync(_deadline)).IsEqualTo(0);
      stopFirst();
      store.Claims.Enqueue([_item(run, Guid.CreateVersion7())]);
      store.Completions.Enqueue([new MultiStreamRunCompletion(run, _commandType, 1, 1, 0)]);

      worker.OnNotification(run.ToString());

      var completion = await completed.Task.WaitAsync(_deadline);
      await Assert.That(completion.RunId).IsEqualTo(run)
        .Because("the notice, not the hour-long poll, started the pass that worked the new run");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Loop_AFullBatch_IsFollowedAtOnceByAnotherPassAsync() {
    var store = new LoopStore();
    var run = Guid.CreateVersion7();
    store.Claims.Enqueue([_item(run, Guid.CreateVersion7())]);
    store.Claims.Enqueue([_item(run, Guid.CreateVersion7())]);
    var worker = _worker(store, new MultiStreamCommandOptions { BatchSize = 1 });
    var passes = new List<int>();
    var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnPassCompleted += claimed => {
      lock (passes) {
        passes.Add(claimed);
        if (claimed == 0) {
          drained.TrySetResult();
        }
      }
    };

    await worker.StartAsync(CancellationToken.None);
    try {
      await drained.Task.WaitAsync(_deadline);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(passes.Take(3)).IsEquivalentTo([1, 1, 0], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a full batch may have more behind it, so the next pass runs without waiting for a notice");
  }

  [Test]
  public async Task Loop_AFailedPass_IsReportedAndTheNextPassRunsAsync() {
    var store = new LoopStore { ClaimFailure = new InvalidOperationException("database unavailable") };
    var worker = _worker(store);
    var (failed, stopFailed) = _nextPass(worker);
    var (next, _) = _nextPass(worker, claimed => claimed == 1);
    var run = Guid.CreateVersion7();

    await worker.StartAsync(CancellationToken.None);
    try {
      await Assert.That(await failed.WaitAsync(_deadline)).IsEqualTo(0);
      stopFailed();
      store.Claims.Enqueue([_item(run, Guid.CreateVersion7())]);
      worker.RequestImmediateRun();

      await Assert.That(await next.WaitAsync(_deadline)).IsEqualTo(1)
        .Because("a failed pass never stops the worker");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Loop_WaitsForTheSchemaBeforeItsFirstPassAsync() {
    var store = new LoopStore();
    var gate = new LoopGate();
    var worker = _worker(store, gate: gate);
    var (first, _) = _nextPass(worker);

    await worker.StartAsync(CancellationToken.None);
    try {
      await gate.Waiting.WaitAsync(_deadline);
      await Assert.That(store.ClaimCount).IsEqualTo(0)
        .Because("the worker is parked on the schema gate, so nothing has been claimed");

      gate.Open();

      await Assert.That(await first.WaitAsync(_deadline)).IsEqualTo(0);
      await Assert.That(store.ClaimCount).IsGreaterThanOrEqualTo(1);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Loop_StoppingMidStream_EndsTheLoopWithoutRecordingTheUnfinishedStreamAsync() {
    var store = new LoopStore();
    var handler = new LoopHandler { BlockUntilCanceled = true };
    store.Claims.Enqueue([_item(Guid.CreateVersion7(), Guid.CreateVersion7())]);
    var worker = _worker(store, handler: handler);

    await worker.StartAsync(CancellationToken.None);
    await handler.Entered.WaitAsync(_deadline);
    await worker.StopAsync(CancellationToken.None);

    await worker.ExecuteTask!.WaitAsync(_deadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(store.CompleteCount).IsEqualTo(0)
      .Because("the stream did not finish; its lease lapses and it is offered again");
  }

  [Test]
  [Arguments(false, true, true)]
  [Arguments(true, false, true)]
  [Arguments(true, true, false)]
  public async Task Loop_DisabledUnconfiguredOrWithoutHandlers_EndsWithoutClaimingAsync(bool enabled, bool storeConfigured, bool withHandler) {
    var store = new LoopStore { Configured = storeConfigured };
    var worker = _worker(store, new MultiStreamCommandOptions { Enabled = enabled }, withHandler: withHandler);

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_deadline);

    await Assert.That(store.ClaimCount).IsEqualTo(0);
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Loop_InvalidOptions_FailsTheWorkerAtStartAsync() {
    var worker = _worker(new LoopStore(), new MultiStreamCommandOptions { BatchSize = 0 });

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(async () => await worker.ExecuteTask!.WaitAsync(_deadline)).Throws<InvalidOperationException>();
    await worker.StopAsync(CancellationToken.None);
  }

  // --- fakes -----------------------------------------------------------------------------------

  /// <summary>The loop tests' command.</summary>
  public sealed record LoopProbeCommand(Guid RunId, IReadOnlyList<Guid> StreamIds) : IMultiStreamCommand;

  /// <summary>Succeeds, or parks until canceled and signals that it entered.</summary>
  public sealed class LoopHandler : IMultiStreamCommandHandler<LoopProbeCommand> {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockUntilCanceled { get; init; }

    public Task Entered => _entered.Task;

    public async ValueTask HandleStreamAsync(LoopProbeCommand command, Guid streamId, CancellationToken cancellationToken) {
      _entered.TrySetResult();
      if (BlockUntilCanceled) {
        await Task.Delay(Timeout.Infinite, cancellationToken);
      }
    }
  }

  private sealed class LoopInstance : IServiceInstanceProvider {
    public Guid InstanceId => _instance;
    public string ServiceName => "loop";
    public string HostName => "loop-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = _instance, ServiceName = "loop", HostName = "loop-host", ProcessId = 1 };
  }

  private sealed class LoopGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Waiting => _waiting.Task;

    public bool IsReady => _open.Task.IsCompleted;

    public void Open() => _open.TrySetResult();

    public void MarkReady() => Open();

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waiting.TrySetResult();
      return _open.Task.WaitAsync(cancellationToken);
    }
  }

  private sealed class LoopStore : IMultiStreamCommandStore {
    private int _claims;
    private int _completes;

    public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<MultiStreamWorkItem>> Claims { get; } = new();

    public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<MultiStreamRunCompletion>> Completions { get; } = new();

    public Exception? ClaimFailure { get; set; }

    public bool Configured { get; init; } = true;

    public bool IsConfigured => Configured;

    public int ClaimCount => Volatile.Read(ref _claims);

    public int CompleteCount => Volatile.Read(ref _completes);

    public Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken) =>
      throw new NotSupportedException("The worker never records runs.");

    public Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _claims);
      if (Interlocked.Exchange(ref _failureTaken, 1) == 0 && ClaimFailure is { } failure) {
        throw failure;
      }
      return Task.FromResult<IReadOnlyList<MultiStreamWorkItem>>(Claims.TryDequeue(out var items) ? items : []);
    }

    private int _failureTaken;

    public Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
        Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _completes);
      return Task.FromResult<IReadOnlyList<MultiStreamRunCompletion>>(Completions.TryDequeue(out var c) ? c : []);
    }

    public Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) =>
      throw new NotSupportedException("The worker never reads a run.");

    public Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) => Task.FromResult(0);
  }
}
